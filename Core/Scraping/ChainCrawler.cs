using Microsoft.Data.Sqlite;
using Microsoft.Playwright;
using AngleSharp.Dom;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Core.Util;

namespace QsrPriceBenchmarks.Core.Scraping;

/// <summary>
/// Step 1: crawl a chain's own website to discover every restaurant
/// location, two levels deep (province -&gt; district -&gt; restaurant), or one level
/// deep when a province page lists restaurants directly.
///
/// Key subtlety carried over from the Python prototype: a province page may
/// list either (a) restaurant cards directly, or (b) district
/// navigation links that themselves need to be sub-crawled, or sometimes
/// both mixed together on the same page. A card counts as "direct" only
/// when it has both a name AND an address containing a digit — bare
/// navigation links (district names dressed up as cards, sometimes with a
/// marketing slogan instead of a real address) must NOT be treated as
/// restaurants, or the real district page underneath is never visited.
/// </summary>
public static class ChainCrawler
{
    public static async Task CrawlAsync(
        ChainConfig chain, SqliteConnection conn,
        IProgress<string>? progress = null, CancellationToken ct = default,
        IProgress<int>? onLocationsChanged = null)
    {
        if (chain.RootUrl is null)
        {
            progress?.Report($"  Skipping — {chain.Name} has no chain website (TG-only).");
            return;
        }

        long qsrId = Repository.QsrId(conn, chain.Name);

        // Timestamp captured before any UpsertLocation stamps LAST_SEEN_AT, so
        // every location seen this crawl ends up with LAST_SEEN_AT >= crawlStart
        // and anything older (or never stamped) is a candidate for soft-close.
        string crawlStart = Repository.UtcStamp();

        await using BrowserSession session = await BrowserSession.LaunchAsync();
        IPage page = session.Page;

        progress?.Report($"  Crawling {chain.RootUrl}");
        string rootHtml = await PageFetcher.FetchHtmlAsync(
            page, chain.RootUrl, waitForSelector: chain.ContentSelector);
        IDocument rootDoc = await PageFetcher.ParseHtmlAsync(rootHtml, chain.RootUrl);

        List<string> provinceLinks = UrlUtils.ParseDepthLinks(rootDoc, chain.RootUrl, depthOffset: 1);
        progress?.Report($"  ✓ {provinceLinks.Count} provinces found");

        int total = 0;
        HashSet<string> districts = new(StringComparer.OrdinalIgnoreCase);
        List<string> failed = new(); // province/district pages we had to skip

        // Upsert each card and report how many were stored. Cards ProcessCard
        // rejects (test slugs, unparseable URLs) are skipped without counting.
        int RecordCards(IEnumerable<RestaurantCard> cards)
        {
            int added = 0;
            foreach (RestaurantCard rec in cards)
            {
                if (ProcessCard(conn, chain, rec, qsrId, districts))
                {
                    added++;
                }
            }
            if (added > 0)
            {
                // Signal after each committed batch (a province's direct cards, or one
                // district page) so a live Locations map can update mid-crawl.
                onLocationsChanged?.Report(added);
            }
            return added;
        }

        for (int i = 0; i < provinceLinks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            string provinceUrl = provinceLinks[i];
            string provinceSlug = UrlUtils.SplitPath(new Uri(provinceUrl).AbsolutePath).LastOrDefault() ?? provinceUrl;
            progress?.Report($"  [{i + 1}/{provinceLinks.Count}] Province: {LogMarkup.Value(provinceSlug)}");

            IDocument provinceDoc;
            try
            {
                string provinceHtml = await PageFetcher.FetchHtmlAsync(page, provinceUrl, ct: ct);
                provinceDoc = await PageFetcher.ParseHtmlAsync(provinceHtml, provinceUrl);
            }
            catch (OperationCanceledException)
            {
                throw; // cancellation must propagate
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                {
                    throw new OperationCanceledException(ct);
                }
                failed.Add(provinceSlug);
                progress?.Report($"    ⚠ skipped province {LogMarkup.Value(provinceSlug)} — {DescribeError(ex)}");
                continue; // skip this province only; keep crawling the rest
            }

            List<RestaurantCard> direct = RestaurantCardParser.Parse(provinceDoc, provinceUrl);
            List<string> districtUrls = UrlUtils.ParseDepthLinks(provinceDoc, provinceUrl, depthOffset: 1);

            // Only treat a card as a confirmed restaurant when it has both a
            // name AND a real address (contains a digit). This excludes
            // district navigation links rendered with the same
            // markup as a restaurant card but carrying a slogan or no
            // address at all instead of a street address.
            HashSet<string> structuredUrls = direct
                .Where(IsRestaurantCard)
                .Select(r => r.Url)
                .ToHashSet();

            direct = direct.Where(r => structuredUrls.Contains(r.Url)).ToList();
            districtUrls = districtUrls.Where(u => !structuredUrls.Contains(u)).ToList();

            total += RecordCards(direct);

            foreach (string provUrl in districtUrls)
            {
                ct.ThrowIfCancellationRequested();

                string provSlug = UrlUtils.SplitPath(new Uri(provUrl).AbsolutePath).LastOrDefault() ?? provUrl;
                progress?.Report($"    ↳ {LogMarkup.Value(provSlug)}");

                IDocument provDoc;
                try
                {
                    string provHtml = await PageFetcher.FetchHtmlAsync(page, provUrl, ct: ct);
                    provDoc = await PageFetcher.ParseHtmlAsync(provHtml, provUrl);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(ct);
                    }
                    failed.Add(provSlug);
                    progress?.Report($"      ⚠ skipped {LogMarkup.Value(provSlug)} — {DescribeError(ex)}");
                    continue; // skip this district only
                }

                List<RestaurantCard> cards = RestaurantCardParser.Parse(provDoc, provUrl);

                // Apply the same filter as the province page: a district page repeats
                // the district navigation menu (links with no name/address), which
                // must NOT be stored as restaurants — that produced NULL rows.
                total += RecordCards(cards.Where(IsRestaurantCard));
            }
        }

        progress?.Report(
            $"  ✓ {total} location(s) across {districts.Count} district(s) recorded");

        if (failed.Count > 0)
        {
            progress?.Report(
                $"  ⚠ Could not scrape {failed.Count} page(s) (skipped): {string.Join(", ", failed)}");
        }

        // Soft-close locations the chain no longer lists — but only after a crawl
        // we can trust. If any page failed, or the crawl somehow found nothing
        // (e.g. the site's markup changed and parsing yielded zero cards), skip
        // the reconcile so a transient error or a parser break can't mass-close
        // every location. A real closure simply waits for the next clean crawl.
        if (failed.Count == 0 && total > 0)
        {
            int closed = Repository.DeactivateStaleLocations(conn, qsrId, crawlStart);
            if (closed > 0)
            {
                progress?.Report($"  ✓ {closed} location(s) no longer listed — marked closed");
            }
        }
        else if (total == 0)
        {
            progress?.Report(
                "  ⚠ Crawl found 0 locations — skipped closed-location reconcile (possible site change)");
        }
        else
        {
            progress?.Report(
                $"  ⚠ {failed.Count} page(s) failed — skipped closed-location reconcile to avoid false closures");
        }
    }

    /// <summary>Short, human-readable reason for a skipped page.</summary>
    private static string DescribeError(Exception ex)
    {
        string msg = ex.Message ?? ex.GetType().Name;
        if (msg.Contains("Timeout", StringComparison.OrdinalIgnoreCase) || ex is System.TimeoutException)
        {
            return "navigation timed out";
        }
        int nl = msg.IndexOf('\n');
        return (nl > 0 ? msg[..nl] : msg).Trim();
    }

    /// <summary>
    /// A parsed card is a real restaurant only when it has a name AND an address
    /// containing a digit (a street number). District navigation links —
    /// which each district page repeats as a menu — have neither, so this keeps
    /// them out of LOCATIONS. Applied to BOTH the province-page and district-page
    /// card sets so navigation links are never stored as restaurants (which had
    /// produced one NULL-name/NULL-address row per district).
    /// </summary>
    public static bool IsRestaurantCard(RestaurantCard rec) =>
        rec.Name is not null && TextNormalization.IsRealAddress(rec.Address);

    /// <summary>
    /// Convert one parsed restaurant card into LOCATIONS rows. Returns false
    /// when the card was skipped (e.g. test slug, or off-tree URL). Records the
    /// card's district slug in <paramref name="districts"/> on success so the
    /// caller can report how many distinct districts were covered.
    /// </summary>
    private static bool ProcessCard(
        SqliteConnection conn, ChainConfig chain, RestaurantCard rec, long qsrId,
        HashSet<string> districts)
    {
        (string Province, string District, string Slug) parts;
        try
        {
            parts = UrlUtils.UrlToParts(rec.Url, chain.RootUrl!);
        }
        catch (ArgumentException)
        {
            return false;
        }

        long? locId = Repository.UpsertLocation(conn, parts.Province, parts.District, parts.Slug, qsrId);
        if (locId is null)
        {
            return false; // test slug — skipped
        }

        districts.Add(parts.District);

        if (rec.Name is not null)
        {
            Repository.UpdateLocationDetails(conn, parts.Slug, rec.Name, rec.Address ?? "", qsrId);
        }

        return true;
    }
}
