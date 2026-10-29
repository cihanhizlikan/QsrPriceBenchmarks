using Microsoft.Data.Sqlite;
using Microsoft.Playwright;
using AngleSharp.Dom;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Core.Services;
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
    /// <summary>Step 1's phase name in progress reports (the sidebar reads "Crawling provinces 12 / 79").</summary>
    public const string CrawlPhase = "Crawling provinces";

    public static async Task CrawlAsync(
        ChainConfig chain, SqliteConnection conn,
        IProgress<string>? progress = null, CancellationToken ct = default,
        IProgress<int>? onLocationsChanged = null,
        IProgress<StepProgress>? progressCount = null)
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

        List<string> provinceLinks = ChainSiteUrls.ParseDepthLinks(rootDoc, chain.RootUrl, depthOffset: 1);
        progress?.Report($"  ✓ {provinceLinks.Count} provinces found");

        int total = 0;
        HashSet<string> districts = new(StringComparer.OrdinalIgnoreCase);
        // Slugs already counted this crawl. The same restaurant can appear on
        // several pages (its province page AND its district page); the upsert
        // handles that fine, but counting each sighting inflated the reported
        // total well past the real number of locations in the database.
        HashSet<string> seenSlugs = new(StringComparer.OrdinalIgnoreCase);
        List<string> failed = []; // province/district pages we had to skip

        // Upsert each card and report how many distinct locations were stored.
        // Cards ProcessCard rejects (test slugs, unparseable URLs) are skipped
        // without counting; re-sightings of a slug already counted this crawl
        // still refresh the row's details but don't count again.
        int RecordCards(IEnumerable<RestaurantCard> cards)
        {
            int added = 0;
            foreach (RestaurantCard rec in cards)
            {
                if (ProcessCard(conn, chain, rec, qsrId, districts, seenSlugs))
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
            string provinceSlug = ChainSiteUrls.SplitPath(new Uri(provinceUrl).AbsolutePath).LastOrDefault() ?? provinceUrl;
            progress?.Report($"  [{i + 1}/{provinceLinks.Count}] Province: {LogMarkup.Value(provinceSlug)}");
            // Provinces are the unit: their count is known before the first is visited, where districts appear
            // only as each province is read.
            progressCount?.Report(new StepProgress(CrawlPhase, i, provinceLinks.Count));

            IDocument? provinceDoc = await TryFetchDocumentAsync(page, provinceUrl, ct, ex =>
            {
                failed.Add(provinceSlug);
                progress?.Report($"    ⚠ skipped province {LogMarkup.Value(provinceSlug)} — {DescribeError(ex)}");
            });
            if (provinceDoc is null)
            {
                continue; // skip this province only; keep crawling the rest
            }

            List<RestaurantCard> direct = RestaurantCardParser.Parse(provinceDoc, provinceUrl);
            List<string> districtUrls = ChainSiteUrls.ParseDepthLinks(provinceDoc, provinceUrl, depthOffset: 1);

            // Only treat a card as a confirmed restaurant when it has both a
            // name AND a real address (contains a digit). This excludes
            // district navigation links rendered with the same
            // markup as a restaurant card but carrying a slogan or no
            // address at all instead of a street address.
            HashSet<string> structuredUrls = [.. direct
                .Where(IsRestaurantCard)
                .Select(r => r.Url)];

            direct = [.. direct.Where(r => structuredUrls.Contains(r.Url))];
            districtUrls = [.. districtUrls.Where(u => !structuredUrls.Contains(u))];

            total += RecordCards(direct);

            foreach (string provUrl in districtUrls)
            {
                ct.ThrowIfCancellationRequested();

                string provSlug = ChainSiteUrls.SplitPath(new Uri(provUrl).AbsolutePath).LastOrDefault() ?? provUrl;
                progress?.Report($"    ↳ {LogMarkup.Value(provSlug)}");

                IDocument? provDoc = await TryFetchDocumentAsync(page, provUrl, ct, ex =>
                {
                    failed.Add(provSlug);
                    progress?.Report($"      ⚠ skipped {LogMarkup.Value(provSlug)} — {DescribeError(ex)}");
                });
                if (provDoc is null)
                {
                    continue; // skip this district only
                }

                List<RestaurantCard> cards = RestaurantCardParser.Parse(provDoc, provUrl);

                // Apply the same filter as the province page: a district page repeats
                // the district navigation menu (links with no name/address), which
                // must NOT be stored as restaurants — that produced NULL rows.
                total += RecordCards(cards.Where(IsRestaurantCard));
            }
        }

        progressCount?.Report(new StepProgress(CrawlPhase, provinceLinks.Count, provinceLinks.Count));
        progress?.Report(
            $"  ✓ {total} distinct location(s) across {districts.Count} district(s) recorded");

        if (failed.Count > 0)
        {
            progress?.Report(
                $"  ⚠ Could not scrape {failed.Count} page(s) (skipped): {string.Join(", ", failed)}");
        }

        ReconcileClosedLocations(conn, qsrId, crawlStart, failed.Count, total, progress);

        // Authoritative tally, taken from the database after the reconcile —
        // the same activeOnly count the Locations tab shows, so the two can
        // never disagree again.
        int activeInDb = Repository.CountLocations(conn, qsrId, activeOnly: true);
        progress?.Report($"  ✓ {activeInDb} active location(s) now in database");
    }

    /// <summary>
    /// Fetch and parse one province or district page. Cancellation always
    /// propagates; any other failure is handed to <paramref name="onSkipped"/>
    /// and null is returned, so the caller skips that page alone and keeps
    /// crawling the rest.
    /// </summary>
    private static async Task<IDocument?> TryFetchDocumentAsync(
        IPage page, string url, CancellationToken ct, Action<Exception> onSkipped)
    {
        try
        {
            string html = await PageFetcher.FetchHtmlAsync(page, url, ct: ct);
            return await PageFetcher.ParseHtmlAsync(html, url);
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
            onSkipped(ex);
            return null;
        }
    }

    /// <summary>
    /// Soft-close locations the chain no longer lists — but only after a crawl
    /// we can trust. If any page failed, or the crawl somehow found nothing
    /// (e.g. the site's markup changed and parsing yielded zero cards), skip the
    /// reconcile so a transient error or a parser break can't mass-close every
    /// location. A real closure simply waits for the next clean crawl.
    /// </summary>
    private static void ReconcileClosedLocations(
        SqliteConnection conn, long qsrId, string crawlStart, int failedCount, int total,
        IProgress<string>? progress)
    {
        if (failedCount == 0 && total > 0)
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
                $"  ⚠ {failedCount} page(s) failed — skipped closed-location reconcile to avoid false closures");
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
        int nl = msg.IndexOf('\n', StringComparison.Ordinal);
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
    public static bool IsRestaurantCard(RestaurantCard rec)
    {
        return rec.Name is not null && TextNormalization.IsRealAddress(rec.Address);
    }

    /// <summary>
    /// Convert one parsed restaurant card into LOCATIONS rows. Returns false
    /// when the card was skipped (e.g. test slug, or off-tree URL). Records the
    /// card's district slug in <paramref name="districts"/> on success so the
    /// caller can report how many distinct districts were covered.
    /// </summary>
    private static bool ProcessCard(
        SqliteConnection conn, ChainConfig chain, RestaurantCard rec, long qsrId,
        HashSet<string> districts, HashSet<string> seenSlugs)
    {
        (string Province, string District, string Slug) parts;
        try
        {
            parts = ChainSiteUrls.UrlToParts(rec.Url, chain.RootUrl!);
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

        // Count each location once per crawl, no matter how many pages list it.
        return seenSlugs.Add(parts.Slug);
    }
}
