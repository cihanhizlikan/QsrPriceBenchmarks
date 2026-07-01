using AngleSharp.Dom;
using Microsoft.Data.Sqlite;
using Microsoft.Playwright;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Core.Util;

namespace QsrPriceBenchmarks.Core.Scraping;

/// <summary>
/// Step 2: scrape every PLATFORM_LOCATIONS row for a chain on Tıkla Gelsin.
///
/// TG is a Next.js app. The first tab ("Popüler Ürünler") is server-rendered
/// and its product data lives only in a <c>self.__next_f.push(...)</c> JSON
/// payload — the static HTML grid has zero real DOM card nodes for it.
/// Clicking through to subsequent tabs makes React render real DOM nodes for
/// that tab (and the JSON payload does NOT update on click — it's a one-time
/// SSR snapshot) — so those tabs must be read from the live DOM instead.
/// </summary>
public static class TgScraper
{
    private const string TgBaseUrl = "https://www.tiklagelsin.com/restoran/";

    /// <summary>One TG page's own NAME/ADDRESS plus its scraped (name, price) menu items.</summary>
    private readonly record struct TgPageData(
        string? Name, string? Address, List<(string Name, decimal Price)> Items,
        int FirstTabCount = 0, int TabsAttempted = 0, int TabsWithItems = 0, string? Reason = null);

    public static async Task ScrapeAsync(
        ChainConfig chain, SqliteConnection conn,
        IProgress<string>? progress = null, CancellationToken ct = default,
        IReadOnlyCollection<string>? tabFilter = null,
        long? resumeRunId = null)
    {
        var qsrId = Repository.QsrId(conn, chain.Name);
        var platformName = chain.PrimaryPlatform; // single platform today: "Tıkla Gelsin"
        var platformId = Repository.PlatformId(conn, platformName);
        var platformSlug = chain.PlatformSlugs[platformName];

        await HarvestSitemapSlugsAsync(chain, conn, qsrId, platformId, platformSlug, progress);

        var tabs = Repository.LoadScrapeTabs(conn, qsrId, platformId);
        if (tabFilter is not null)
        {
            // Keep only the user-selected tabs, preserving the configured order.
            var allowed = new HashSet<string>(tabFilter, StringComparer.Ordinal);
            tabs = tabs.Where(allowed.Contains).ToList();
        }
        progress?.Report($"  Tabs: {string.Join(", ", tabs)}");

        long runId = resumeRunId ?? Repository.StartScrapeRun(conn, qsrId);
        progress?.Report(resumeRunId is null
            ? $"  Scrape run {runId} started"
            : $"  Resuming scrape run {runId}");

        List<(long Id, string Slug)> queue = resumeRunId is null
            ? LoadFullQueue(conn, qsrId, platformId)
            : LoadQueueMissingFromRun(conn, qsrId, platformId, runId);
        progress?.Report($"  Scraping {queue.Count} platform location(s)");

        await using BrowserSession session = await BrowserSession.LaunchAsync();
        IPage page = session.Page;

        try
        {
            for (int i = 0; i < queue.Count; i++)
            {
                (long plId, string slug) = queue[i];
                progress?.Report($"  [{i + 1}/{queue.Count}] {slug}");

                ct.ThrowIfCancellationRequested();
                TgPageData pageData = await ScrapeOnePageWithRetryAsync(
                    page, platformSlug, slug, tabs, progress, ct);

                // Persist the restaurant's own NAME/ADDRESS (needed for Step 3
                // geocoding and matching), then its menu-item price snapshots.
                if (pageData.Name is not null || pageData.Address is not null)
                {
                    Repository.UpdatePlatformLocation(conn, plId,
                        name: pageData.Name, address: pageData.Address);
                }

                IReadOnlyList<(string Name, decimal Price)> items = pageData.Items;
                if (items.Count == 0)
                {
                    string why = pageData.Reason ?? "active page, but SSR + DOM tabs both parsed nothing";
                    progress?.Report($"      No menu items — {why}");
                }
                else
                {
                    foreach ((string name, decimal price) in items)
                    {
                        long menuItemId = Repository.GetOrCreateMenuItem(conn, qsrId, name);
                        Repository.InsertSnapshot(conn, runId, plId, menuItemId, price);
                    }
                }

                if (pageData.Reason is null)
                {
                    progress?.Report(
                        $"      → {items.Count} item(s)  [first tab {pageData.FirstTabCount}, " +
                        $"other tabs {pageData.TabsWithItems}/{pageData.TabsAttempted} produced items]");
                }
                else
                {
                    progress?.Report($"      → {items.Count} menu item(s)");
                }
            }
        }
        finally
        {
            // Always finalize the run — even on error or cancellation — so it is
            // never left stuck in the "running" (no FINISHED_AT) state.
            Repository.FinishScrapeRun(conn, runId);
            progress?.Report($"  ✓ Run {runId} finished.");
        }
    }

    private const int MaxPageAttempts = 3;

    /// <summary>
    /// Scrape one page with a few retries so a momentary network blip doesn't
    /// abort the whole run. Transient Playwright/timeout errors are retried up to
    /// <see cref="MaxPageAttempts"/> times; cancellation is never retried.
    /// </summary>
    private static async Task<TgPageData> ScrapeOnePageWithRetryAsync(
        IPage page, string platformSlug, string slug, List<string> tabs,
        IProgress<string>? progress, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxPageAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await ScrapeOnePageAsync(page, platformSlug, slug, tabs, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (attempt >= MaxPageAttempts)
                {
                    progress?.Report($"      ⚠ skipped after {MaxPageAttempts} attempts: {ex.Message}");
                    return new TgPageData(null, null, new List<(string, decimal)>(),
                        Reason: $"failed after {MaxPageAttempts} attempts");
                }
                progress?.Report(
                    $"      ⚠ transient error (attempt {attempt}/{MaxPageAttempts}), retrying: {ex.Message}");
                await Task.Delay(2_000, ct);
            }
        }
        return new TgPageData(null, null, new List<(string, decimal)>(), Reason: "unreachable");
    }

    /// <summary>
    /// Scrape one TG restaurant page across every configured tab.
    /// Returns deduplicated (name, price) pairs — later tabs never overwrite
    /// an item name already seen from an earlier tab.
    /// </summary>
    private static async Task<TgPageData> ScrapeOnePageAsync(
        IPage page, string platformSlug, string slug, List<string> tabs, CancellationToken ct = default)
    {
        var url = $"{TgBaseUrl}{platformSlug}/{slug}?gel-al";
        var results = new List<(string, decimal)>();
        var seen = new HashSet<string>();

        // Closing the page on cancellation makes any pending navigation/wait
        // throw at once, so Cancel takes effect mid-page rather than only at
        // the next per-restaurant token check.
        await using var reg = ct.Register(() =>
        {
            try { _ = page.CloseAsync(); } catch { /* already closing */ }
        });

        try
        {
            await page.GotoAsync(url, new PageGotoOptions
            {
                // 'Commit' returns as soon as the server response arrives, instead
                // of waiting for DOMContentLoaded — TG's heavy third-party scripts
                // can otherwise delay that event past the timeout. Hydration is
                // confirmed separately by the tab-bar wait below.
                WaitUntil = WaitUntilState.Commit,
                Timeout = 60_000,
            });
        }
        catch (TimeoutException)
        {
            return new TgPageData(null, null, results, Reason: "navigation timed out before commit");
        }

        await PageFetcher.DismissCookieBannerAsync(page);

        // Wait for the Next.js tab bar to appear — confirms React hydrated
        // and the SSR menu content is ready to read.
        try
        {
            await page.WaitForSelectorAsync(".ant-tabs-tab-btn",
                new PageWaitForSelectorOptions { Timeout = 15_000 });
        }
        catch (TimeoutException)
        {
            await page.WaitForTimeoutAsync(4_000);
        }

        for (int i = 0; i < 6; i++)
        {
            await page.EvaluateAsync("window.scrollBy(0, 600)");
            await page.WaitForTimeoutAsync(300);
        }

        var firstPageHtml = await WaitForMenuPayloadAsync(page);

        // Inactive-restaurant / Next.js server-error detection: these pages
        // render only navigation/footer, with no product data at all.
        if (firstPageHtml.Contains("E{\"digest\":"))
        {
            return new TgPageData(null, null, results, Reason: "inactive/closed page (digest marker)");
        }

        // The restaurant's own name/address are NOT in the static HTML. The
        // address is revealed only after opening the "Tümü" info modal, so this
        // must be a live page interaction (ScrapeRestaurantInfoAsync clears any
        // late consent banner and opens the modal itself).
        (string? restName, string? restAddress) = await ScrapeRestaurantInfoAsync(page);

        // Temporarily-out-of-order restaurants still render their menu, so they
        // would otherwise be recorded with (stale) prices. TG shows a popup
        // reading "Restoran Hizmet Dışı / Restoran geçici olarak hizmet
        // verememektedir." — detect it and report zero items (keeping the
        // name/address) so the location surfaces in the "Unscraped Platform
        // Locations" tab instead of getting a misleading price snapshot.
        if (await IsOutOfOrderAsync(page))
        {
            return new TgPageData(restName, restAddress, results,
                Reason: "temporarily out of order (Restoran Hizmet Dışı)");
        }

        int firstTabCount = 0, tabsAttempted = 0, tabsWithItems = 0;

        for (int tabIdx = 0; tabIdx < tabs.Count; tabIdx++)
        {
            ct.ThrowIfCancellationRequested();
            var tabName = tabs[tabIdx];
            List<(string, decimal)> tabItems;

            if (tabIdx == 0)
            {
                // First tab: SSR — product data lives in the Next.js script
                // JSON payload, not real DOM nodes. Section headings aren't
                // rendered in the static HTML, so pass tabName=null and take
                // every item found.
                tabItems = NextJsTabParser.Parse(firstPageHtml, tabName: null)
                    .Select(m => (m.Name, m.Price))
                    .ToList();
            }
            else
            {
                tabsAttempted++;

                // Subsequent tabs: click, then read the live re-rendered DOM.
                var clicked = await TryClickTabAsync(page, tabName);
                if (clicked)
                {
                    try
                    {
                        await page.WaitForFunctionAsync($$"""
                            () => {
                                const g = document.querySelector('.grid.lg\\:grid-cols-2');
                                return g && g.querySelectorAll('[id="restaurant-card-title"]').length > 0;
                            }
                            """, options: new PageWaitForFunctionOptions { Timeout = 3_000 });
                    }
                    catch (TimeoutException)
                    {
                        await page.WaitForTimeoutAsync(600);
                    }
                }

                var html = await page.ContentAsync();
                tabItems = await ExtractFromLiveDomAsync(html, url);
            }

            int newCount = 0;
            foreach (var (name, price) in tabItems)
            {
                if (seen.Add(name))
                {
                    results.Add((name, price));
                    newCount++;
                }
            }

            if (tabIdx == 0)
                firstTabCount = newCount;
            else if (newCount > 0)
                tabsWithItems++;
        }

        return new TgPageData(restName, restAddress, results,
            FirstTabCount: firstTabCount, TabsAttempted: tabsAttempted, TabsWithItems: tabsWithItems);
    }

    /// <summary>
    /// Poll the FULL page HTML (<see cref="IPage.ContentAsync"/>) until the SSR
    /// menu payload the parser actually needs has fully streamed in — i.e. until
    /// <see cref="NextJsTabParser"/> yields at least one item — or until an
    /// inactive (digest) page is detected, or the timeout elapses. Returns the
    /// last HTML observed.
    ///
    /// This is more reliable than waiting on a body-marker string: with
    /// commit-stage navigation the <c>self.__next_f.push([1,"…"])</c> block can
    /// still be arriving, so the marker may be present while its enclosing block
    /// is unterminated — which the parser's regex (it requires the closing
    /// <c>"])</c>) skips, yielding zero items. Parsing the complete document is
    /// the only signal that the payload is genuinely ready.
    /// </summary>
    public static async Task<string> WaitForMenuPayloadAsync(IPage page, int timeoutMs = 12_000)
    {
        var html = await page.ContentAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (html.Contains("E{\"digest\":")) break;            // inactive page — nothing to await
            if (NextJsTabParser.Parse(html, tabName: null).Count > 0) break;
            await page.WaitForTimeoutAsync(750);
            html = await page.ContentAsync();
        }
        return html;
    }

    /// <summary>
    /// Scrape a TG restaurant's own NAME and full ADDRESS. The name comes from
    /// the page heading (&lt;h1&gt;); the address is read from the "Tümü" info
    /// modal (<c>#restaurant-info-open-modal</c>) — the only reliable source,
    /// matching the Python prototype. The modal trigger is <c>hidden sm:block</c>,
    /// so a desktop viewport (≥640px; our context is 1280px) is required.
    /// </summary>
    public static async Task<(string? Name, string? Address)> ScrapeRestaurantInfoAsync(IPage page)
    {
        // Clear any consent banner first. This makes the method self-sufficient:
        // the full scraper already dismisses earlier, but the integration test
        // calls this directly without that step, and an undismissed banner both
        // supplies its own <h1> and covers the address-modal trigger.
        await PageFetcher.DismissCookieBannerAsync(page);

        // Name — the restaurant page heading (<h1>), per the prototype; fall
        // back to <h2>/<title> only when no <h1> is present.
        string? name = null;
        try
        {
            name = await page.EvaluateAsync<string?>("""
                () => {
                    const norm = s => (s || '').replace(/\s+/g, ' ').trim();
                    const bad = /çerez|cookie|tercih|kabul/i;  // consent-banner headings
                    const heads = [...document.querySelectorAll('h1, h2')]
                        .map(h => norm(h.textContent))
                        .filter(t => t && !bad.test(t));
                    if (heads.length) return heads[0];
                    const t = norm(document.title).split(/\s[|\-–—]\s/)[0];
                    return (t && !bad.test(t)) ? t : null;
                }
                """);
        }
        catch { /* leave null */ }

        // Address — read only from the "Tümü" info modal (the confirmed source).
        string? address = await TryAddressFromModalAsync(page);

        return (Norm(name), Norm(address));
    }

    /// <summary>
    /// Open the "Tümü" info modal and read the restaurant's address. Confirmed
    /// live structure:
    /// <code>
    ///   &lt;div class="ant-modal-body"&gt;
    ///     …&lt;div class="m-2"&gt;
    ///         &lt;div class="text-bw-default"&gt;Adres&lt;/div&gt;
    ///         &lt;div class="text-bw-light2"&gt;Cebeci Mah. Cemal Gürsel Cad. No: 45&lt;/div&gt;
    ///       &lt;/div&gt;…
    ///   &lt;/div&gt;
    /// </code>
    /// The address is the <c>text-bw-light2</c> sibling of the <c>text-bw-default</c>
    /// label reading "Adres". Returns null on any failure (trigger absent/not
    /// clickable, modal never hydrates, label missing).
    /// </summary>
    private static async Task<string?> TryAddressFromModalAsync(IPage page)
    {
        // Opening the Ant Design modal and waiting for it to hydrate is timing
        // sensitive and intermittently flaky, so try a few times before giving
        // up — this is what makes the integration tests reliable.
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            string? addr = await TryReadAddressOnceAsync(page);
            if (!string.IsNullOrWhiteSpace(addr))
            {
                return addr;
            }
            try { await page.Keyboard.PressAsync("Escape"); } catch { /* ignore */ }
            await page.WaitForTimeoutAsync(500 * attempt);
        }
        return null;
    }

    private static async Task<string?> TryReadAddressOnceAsync(IPage page)
    {
        try
        {
            // A late consent banner can reappear and cover the trigger; clear it
            // each attempt. Then scroll the trigger into view and click it,
            // letting Playwright auto-wait for it to hydrate and be actionable.
            await PageFetcher.DismissCookieBannerAsync(page);
            try
            {
                ILocator trigger = page.Locator("#restaurant-info-open-modal");
                await trigger.ScrollIntoViewIfNeededAsync(
                    new LocatorScrollIntoViewIfNeededOptions { Timeout = 5_000 });
                await trigger.ClickAsync(new LocatorClickOptions { Timeout = 8_000 });
            }
            catch (TimeoutException)
            {
                return null;  // trigger never hydrated / not clickable
            }
            catch (PlaywrightException)
            {
                return null;  // trigger absent on this page
            }

            // Wait for the "Adres" label inside the modal — Ant Design animates
            // the container in and React hydrates its content a moment later, so
            // waiting for the container alone would read it empty.
            try
            {
                await page.WaitForFunctionAsync("""
                    () => {
                        const modal = document.querySelector('div.ant-modal-body');
                        if (!modal) return false;
                        return [...modal.querySelectorAll('div.text-bw-default')]
                            .some(el => el.innerText.trim() === 'Adres');
                    }
                    """, options: new PageWaitForFunctionOptions { Timeout = 6_000 });
            }
            catch (TimeoutException)
            {
                return null;
            }

            string? addr = await page.EvaluateAsync<string?>("""
                () => {
                    const modal = document.querySelector('div.ant-modal-body');
                    if (!modal) return null;
                    for (const el of modal.querySelectorAll('div.text-bw-default')) {
                        if (el.innerText.trim() === 'Adres') {
                            const sib = el.nextElementSibling;
                            if (sib && sib.classList.contains('text-bw-light2'))
                                return sib.innerText.trim() || null;
                        }
                    }
                    return null;
                }
                """);

            try { await page.Keyboard.PressAsync("Escape"); } catch { /* ignore */ }
            await page.WaitForTimeoutAsync(300);
            return addr;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// True when TG is showing its "restaurant out of service" popup — the text
    /// "Restoran Hizmet Dışı" / "Restoran geçici olarak hizmet verememektedir."
    /// These pages still render a menu, so this guard lets the caller report zero
    /// items and surface the location as unscraped rather than recording stale
    /// prices for a restaurant that can't actually take orders.
    /// </summary>
    private static async Task<bool> IsOutOfOrderAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<bool>("""
                () => {
                    const t = (document.body && document.body.innerText) || '';
                    return t.includes('Hizmet Dışı')
                        || t.includes('geçici olarak hizmet')
                        || t.includes('hizmet verememekte');
                }
                """);
        }
        catch
        {
            return false;
        }
    }

    private static string? Norm(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : System.Net.WebUtility.HtmlDecode(s.Trim());
    private static async Task<bool> TryClickTabAsync(IPage page, string tabName)
    {
        try
        {
            var tabNameJson = System.Text.Json.JsonSerializer.Serialize(tabName);
            var clicked = await page.EvaluateAsync<bool>($$"""
                () => {
                    const target = {{tabNameJson}};
                    const btns = [...document.querySelectorAll('.ant-tabs-tab-btn')];
                    const match = btns.find(el => el.innerText.trim() === target.trim());
                    if (match) {
                        const tab = match.closest('.ant-tabs-tab') || match;
                        tab.click();
                        return true;
                    }
                    return false;
                }
                """);
            return clicked;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Extract (name, price) pairs from the live re-rendered DOM after a tab
    /// click. Uses id="restaurant-card-title" for the name — NOT
    /// id="restaurant-card-description", which holds portion/detail text
    /// like "6 Adet" that must never be stored as the item name.
    /// </summary>
    private static async Task<List<(string, decimal)>> ExtractFromLiveDomAsync(string html, string url)
    {
        var doc = await PageFetcher.ParseHtmlAsync(html, url);
        var grid = doc.QuerySelectorAll("div.grid")
            .FirstOrDefault(g => (g.ClassName ?? "").Contains("lg:grid-cols-2"));
        if (grid is null)
            return new List<(string, decimal)>();

        var results = new List<(string, decimal)>();
        var seen = new HashSet<string>();

        foreach (var titleEl in grid.QuerySelectorAll("#restaurant-card-title"))
        {
            var name = System.Net.WebUtility.HtmlDecode(titleEl.TextContent).Trim();
            if (string.IsNullOrEmpty(name) || !seen.Add(name))
                continue;

            var card = titleEl.Closest("div.flex-col") ?? titleEl.ParentElement;
            var priceEl = card?.QuerySelectorAll("span, div")
                .FirstOrDefault(e => (e.ClassName ?? "").Contains("text-marker") || (e.ClassName ?? "").Contains("text-errorText"));
            if (priceEl is null)
                continue;

            var price = PriceParsing.ParsePrice(priceEl.TextContent);
            if (price is null)
                continue;

            results.Add((name, price.Value));
        }

        return results;
    }

    /// <summary>
    /// Harvest restaurant slugs for this chain from the TG sitemap XML and
    /// upsert them as PLATFORM_LOCATIONS rows.
    /// </summary>
    private static async Task HarvestSitemapSlugsAsync(
        ChainConfig chain, SqliteConnection conn, long qsrId, long platformId, string platformSlug,
        IProgress<string>? progress)
    {
        using var http = new HttpClient();
        // TG blocks non-browser clients on the sitemap (403/empty body), which
        // would leave PLATFORM_LOCATIONS empty and Step 2 with nothing to scrape.
        // Mirror a real browser's headers (same as the integration-test fetch).
        http.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserSession.DefaultUserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd(
            "tr-TR,tr;q=0.9,en-US;q=0.8,en;q=0.7");

        string xml;
        try
        {
            xml = await http.GetStringAsync(chain.SitemapUrl);
        }
        catch (HttpRequestException ex)
        {
            progress?.Report($"  ⚠ Failed to fetch sitemap: {ex.Message}");
            return;
        }

        var prefix = $"https://www.tiklagelsin.com/restoran/{platformSlug}/";
        var slugs = new List<string>();
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(xml, $@"<loc>\s*{System.Text.RegularExpressions.Regex.Escape(prefix)}([^<?\s]+)"))
        {
            slugs.Add(m.Groups[1].Value.TrimEnd('/'));
        }

        progress?.Report($"  ✓ {slugs.Count} {platformSlug} slugs in sitemap");

        // One transaction for the whole batch instead of an implicit commit
        // (fsync) per slug — turns thousands of round-trips into one.
        int inserted = Repository.UpsertPlatformLocationsBulk(conn, qsrId, platformId, slugs);
        int after = CountPlatformLocations(conn, qsrId, platformId);

        progress?.Report($"  ✓ sitemap: {inserted} new slugs ({after} total)");
    }

    private static int CountPlatformLocations(SqliteConnection conn, long qsrId, long platformId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM PLATFORM_LOCATIONS WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat;";
        cmd.Parameters.AddWithValue("$qsr", qsrId);
        cmd.Parameters.AddWithValue("$plat", platformId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Platform locations for this chain that have NO menu snapshot in the given
    /// run yet — i.e. the work still outstanding when resuming an errored run.
    /// </summary>
    private static List<(long Id, string Slug)> LoadQueueMissingFromRun(
        SqliteConnection conn, long qsrId, long platformId, long runId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT pl.ID, pl.SLUG FROM PLATFORM_LOCATIONS pl
            WHERE pl.QSR_ID = $qsr AND pl.PLATFORM_ID = $plat
              AND NOT EXISTS (
                  SELECT 1 FROM MENU_SNAPSHOTS ms
                  WHERE ms.PLATFORM_LOCATION_ID = pl.ID AND ms.SCRAPE_RUN_ID = $run);
            """;
        cmd.Parameters.AddWithValue("$qsr", qsrId);
        cmd.Parameters.AddWithValue("$plat", platformId);
        cmd.Parameters.AddWithValue("$run", runId);

        List<(long, string)> result = new();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        return result;
    }

    private static List<(long Id, string Slug)> LoadFullQueue(SqliteConnection conn, long qsrId, long platformId)
    {        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ID, SLUG FROM PLATFORM_LOCATIONS WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat;";
        cmd.Parameters.AddWithValue("$qsr", qsrId);
        cmd.Parameters.AddWithValue("$plat", platformId);

        var result = new List<(long, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetInt64(0), reader.GetString(1)));
        return result;
    }
}
