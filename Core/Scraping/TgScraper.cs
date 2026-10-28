using System.Globalization;
using AngleSharp.Dom;
using Microsoft.Data.Sqlite;
using Microsoft.Playwright;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;

namespace QsrPriceBenchmarks.Core.Scraping;

/// <summary>
/// Step 2: scrape every PLATFORM_LOCATIONS row for a chain on Tıkla Gelsin.
///
/// TG is a Next.js app. The first tab ("Popüler Ürünler") is server-rendered
/// and its product data lives only in a <c>self.__next_f.push(...)</c> JSON
/// payload; the live DOM gains its cards only once React hydrates (measured
/// 2026-10-28: 14 cards on Popeyes' first tab after hydration).
/// Clicking through to subsequent tabs makes React render real DOM nodes for
/// that tab (and the JSON payload does NOT update on click — it's a one-time
/// SSR snapshot) — so every tab is read from the live DOM, the first one
/// falling back to the payload when its cards have not painted.
/// </summary>
public static class TgScraper
{
    private const string TgBaseUrl = "https://www.tiklagelsin.com/restoran/";

    /// <summary>
    /// One TG page's own NAME/ADDRESS, its scraped (name, price) menu items (deduplicated across tabs), the tab
    /// bar it showed, and the product names each scraped tab displayed, before deduplication.
    /// <see cref="Reason"/> is set when the page yielded no menu for a reason other than its tabs.
    /// </summary>
    public readonly record struct TgPageData(
        string? Name, string? Address, List<(string Name, decimal Price)> Items,
        string? Reason = null, List<string>? PageTabs = null,
        IReadOnlyDictionary<string, List<string>>? ItemNamesPerTab = null);

    public static async Task ScrapeAsync(
        ChainConfig chain, SqliteConnection conn,
        IProgress<string>? progress = null, CancellationToken ct = default,
        IReadOnlyCollection<string>? tabFilter = null,
        long? resumeRunId = null)
    {
        long qsrId = Repository.QsrId(conn, chain.Name);
        string platformName = chain.PrimaryPlatform; // single platform today: "Tıkla Gelsin"
        long platformId = Repository.PlatformId(conn, platformName);
        string platformSlug = chain.PlatformSlugs[platformName];

        await HarvestSitemapSlugsAsync(chain, conn, qsrId, platformId, platformSlug, progress);

        (HashSet<string> excludedTabs, HashSet<string> knownTabs) =
            ResolveTabs(conn, qsrId, platformId, tabFilter, progress);

        // Union of tab names actually seen on pages during this run — feeds the
        // end-of-run soft-delete reconciliation of SCRAPE_TABS.
        HashSet<string> observedTabs = new(StringComparer.Ordinal);
        int unreadPages = 0;

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
                    page, platformSlug, slug, excludedTabs, progress, ct);

                if (IsTechnicalFailure(pageData))
                {
                    unreadPages++;
                }

                RecordDiscoveredTabs(conn, qsrId, platformId, pageData, knownTabs, observedTabs, progress);
                RecordPage(conn, qsrId, runId, plId, pageData, progress);
            }

            // Disable (never delete) every known tab no page READ in this run showed. This line is inside the
            // try, so a cancelled or failed run never reaches it. Pages that could not be read are not waited
            // for: over hundreds of live pages one timeout is routine, and requiring a flawless run meant a
            // vanished tab was practically never disabled. Disabling one too early costs nothing lasting — the
            // next page that shows it re-enables it, ticked (RecordDiscoveredTabs).
            if (ShouldDisableUnseenTabs(resumeRunId is not null, observedTabs.Count))
            {
                List<string> deactivated =
                    Repository.ReconcileScrapeTabs(conn, qsrId, platformId, observedTabs);
                foreach (string gone in deactivated)
                {
                    progress?.Report(
                        $"  ✂ Tab \"{gone}\" appeared on no location read this run — disabled until it reappears");
                }
                if (unreadPages > 0 && deactivated.Count > 0)
                {
                    progress?.Report($"    ({unreadPages} location(s) could not be read this run)");
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

    /// <summary>
    /// The tabs this run must NOT scrape — the known tabs the user left unticked — and the known-tab set new tabs
    /// are discovered against. A selection is read as exclusions rather than as the whole list because the
    /// pages, not the database, say which tabs exist: a fresh database knows none (its UI selection is empty),
    /// and a tab TG adds mid-run was never offered for ticking. Both are scraped on the visit that finds them.
    /// </summary>
    internal static (HashSet<string> ExcludedTabs, HashSet<string> KnownTabs) ResolveTabs(
        SqliteConnection conn, long qsrId, long platformId,
        IReadOnlyCollection<string>? tabFilter, IProgress<string>? progress)
    {
        List<string> activeTabs = Repository.LoadScrapeTabs(conn, qsrId, platformId);
        HashSet<string> knownTabs = new(activeTabs, StringComparer.Ordinal);
        HashSet<string> excluded = new(StringComparer.Ordinal);
        if (tabFilter is not null)
        {
            excluded.UnionWith(activeTabs.Except(tabFilter, StringComparer.Ordinal));
        }

        if (activeTabs.Count == 0)
        {
            progress?.Report("  ⓘ No tabs known for this chain yet — every tab each page shows is scraped");
        }
        else if (excluded.Count > 0)
        {
            progress?.Report($"  Tabs left out: {string.Join(", ", activeTabs.Where(excluded.Contains))}");
        }
        else
        {
            progress?.Report("  Tabs: every tab each page shows");
        }
        return (excluded, knownTabs);
    }

    /// <summary>
    /// Whether a finished run may disable the known tabs it never saw. Not after a resume, which visits only the
    /// pages a previous run missed; and not when no page showed a tab bar at all, which says the platform failed,
    /// not that every tab vanished. Unread pages do NOT stop it (see the caller).
    /// </summary>
    internal static bool ShouldDisableUnseenTabs(bool resumed, int observedTabCount)
    {
        return !resumed && observedTabCount > 0;
    }

    /// <summary>True when the page could not be read at all, as opposed to being read and empty.</summary>
    private static bool IsTechnicalFailure(TgPageData pageData)
    {
        return pageData.Reason is not null
            && (pageData.Reason.StartsWith("failed after", StringComparison.Ordinal)
                || pageData.Reason.StartsWith("navigation timed out", StringComparison.Ordinal));
    }

    /// <summary>
    /// Adds the page's tab bar to <paramref name="observedTabs"/>, and records any
    /// tab not in <paramref name="knownTabs"/> as selectable from the NEXT run.
    /// </summary>
    private static void RecordDiscoveredTabs(
        SqliteConnection conn, long qsrId, long platformId, TgPageData pageData,
        HashSet<string> knownTabs, HashSet<string> observedTabs, IProgress<string>? progress)
    {
        if (pageData.PageTabs is null)
        {
            return;
        }
        foreach (string pageTab in pageData.PageTabs)
        {
            observedTabs.Add(pageTab);
            if (knownTabs.Add(pageTab))
            {
                // New (or previously soft-deleted) tab spotted live on the
                // platform: selectable from the NEXT run on — the tab list of the
                // run in progress is fixed.
                Repository.UpsertDiscoveredScrapeTab(conn, qsrId, platformId, pageTab);
                progress?.Report(
                    $"      🆕 New tab \"{pageTab}\" discovered — selectable from the next run");
            }
        }
    }

    /// <summary>
    /// Persists one page: the restaurant's own NAME/ADDRESS (needed for Step 3
    /// geocoding and matching), then its menu-item price snapshots, and reports
    /// what was stored.
    /// </summary>
    private static void RecordPage(
        SqliteConnection conn, long qsrId, long runId, long plId, TgPageData pageData,
        IProgress<string>? progress)
    {
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
            IReadOnlyDictionary<string, List<string>> perTab =
                pageData.ItemNamesPerTab ?? new Dictionary<string, List<string>>();
            progress?.Report(
                $"      → {items.Count} item(s)  [{perTab.Count(t => t.Value.Count > 0)}/{perTab.Count} tab(s) " +
                "produced items]");
        }
        else
        {
            progress?.Report($"      → {items.Count} menu item(s)");
        }
    }

    private const int MaxPageAttempts = 3;

    /// <summary>
    /// Scrape one page with a few retries so a momentary network blip doesn't
    /// abort the whole run. Transient Playwright/timeout errors are retried up to
    /// <see cref="MaxPageAttempts"/> times; cancellation is never retried.
    /// </summary>
    private static async Task<TgPageData> ScrapeOnePageWithRetryAsync(
        IPage page, string platformSlug, string slug, IReadOnlySet<string> excludedTabs,
        IProgress<string>? progress, CancellationToken ct)
    {
        for (int attempt = 1; attempt <= MaxPageAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await ScrapePageAsync(page, platformSlug, slug, excludedTabs, ct);
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
                    return new TgPageData(null, null, [],
                        Reason: $"failed after {MaxPageAttempts} attempts");
                }
                progress?.Report(
                    $"      ⚠ transient error (attempt {attempt}/{MaxPageAttempts}), retrying: {ex.Message}");
                await Task.Delay(2_000, ct);
            }
        }
        return new TgPageData(null, null, [], Reason: "unreachable");
    }

    /// <summary>
    /// Scrape one TG restaurant page across every tab its tab bar shows except <paramref name="excludedTabs"/>.
    /// Returns deduplicated (name, price) pairs — later tabs never overwrite
    /// an item name already seen from an earlier tab.
    /// </summary>
    public static async Task<TgPageData> ScrapePageAsync(
        IPage page, string platformSlug, string slug, IReadOnlySet<string> excludedTabs, CancellationToken ct = default)
    {
        string url = $"{TgBaseUrl}{platformSlug}/{slug}?gel-al";
        List<(string, decimal)> results = [];
        HashSet<string> seen = [];

        // Closing the page on cancellation makes any pending navigation/wait
        // throw at once, so Cancel takes effect mid-page rather than only at
        // the next per-restaurant token check.
        await using CancellationTokenRegistration reg = ct.Register(() =>
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

        string firstPageHtml = await WaitForMenuPayloadAsync(page);

        // Inactive-restaurant / Next.js server-error detection: these pages
        // render only navigation/footer, with no product data at all.
        if (firstPageHtml.Contains("E{\"digest\":", StringComparison.Ordinal))
        {
            return new TgPageData(null, null, results, Reason: "inactive/closed page (digest marker)");
        }

        // The page is alive — read the tab bar's actual tab names. This is what
        // drives tab discovery, the end-of-run soft-delete reconciliation and
        // the order the tabs are scraped in below.
        List<string> pageTabs = await ReadPageTabNamesAsync(page);

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
                Reason: "temporarily out of order (Restoran Hizmet Dışı)",
                PageTabs: pageTabs);
        }

        // Scrape this page's own tabs, in the order it shows them, skipping only the ones the user unticked. The
        // page — not the stored list — decides the order, because the tab active on load is the page's first one
        // whatever the stored DISPLAY_ORDER says, and a tab this run discovers is scraped on this very visit.
        Dictionary<string, List<string>> namesPerTab = new(StringComparer.Ordinal);
        string? activeTab = pageTabs.FirstOrDefault();
        foreach (string tabName in pageTabs)
        {
            ct.ThrowIfCancellationRequested();
            if (excludedTabs.Contains(tabName))
            {
                continue;
            }

            if (tabName != activeTab)
            {
                // The previous tab's cards are still on screen when the click lands, so "a card exists" would
                // pass at once and read them again; wait until this tab is active AND the first card has
                // changed. A tab whose first product matches the last one's falls through to the timeout below.
                string previousFirst = await FirstCardTitleAsync(page);
                if (await TryClickTabAsync(page, tabName))
                {
                    activeTab = tabName;
                    try
                    {
                        await page.WaitForFunctionAsync("""
                            ([target, previous]) => {
                                const active = document.querySelector('.ant-tabs-tab-active');
                                if (!active || active.innerText.trim() !== target.trim()) return false;
                                const first = document.querySelector('.grid.lg\\:grid-cols-2 [id^="product-card-"][id$="-title"]');
                                return !!first && first.innerText.trim() !== previous;
                            }
                            """, new[] { tabName, previousFirst }, new PageWaitForFunctionOptions { Timeout = 5_000 });
                    }
                    catch (TimeoutException)
                    {
                        await page.WaitForTimeoutAsync(600);
                    }
                }
            }

            List<(string, decimal)> tabItems = await ExtractFromLiveDomAsync(await page.ContentAsync(), url);
            if (tabItems.Count == 0 && tabName == pageTabs[0])
            {
                // The first tab is also server-rendered: when its live cards have not painted, the payload has it.
                tabItems = [.. NextJsTabParser.Parse(firstPageHtml, tabName: null).Select(m => (m.Name, m.Price))];
            }

            namesPerTab[tabName] = [.. tabItems.Select(i => i.Item1)];
            AddNew(tabItems, results, seen);
        }

        if (pageTabs.Count == 0)
        {
            // No tab bar could be read: the server-rendered first tab is still a menu worth keeping.
            AddNew([.. NextJsTabParser.Parse(firstPageHtml, tabName: null).Select(m => (m.Name, m.Price))], results, seen);
        }

        return new TgPageData(restName, restAddress, results, PageTabs: pageTabs, ItemNamesPerTab: namesPerTab);
    }

    /// <summary>
    /// Appends the items whose names were not seen on an earlier tab — a later tab never overwrites one.
    /// </summary>
    private static void AddNew(List<(string, decimal)> tabItems, List<(string, decimal)> results, HashSet<string> seen)
    {
        foreach ((string name, decimal price) in tabItems)
        {
            if (seen.Add(name))
            {
                results.Add((name, price));
            }
        }
    }

    /// <summary>
    /// Distinct, trimmed tab names from the page's live Ant Design tab bar.
    /// Returns an empty list on any evaluation failure — a page whose tab bar
    /// can't be read simply contributes nothing to discovery/reconciliation.
    /// </summary>
    private static async Task<List<string>> ReadPageTabNamesAsync(IPage page)
    {
        try
        {
            string[]? names = await page.EvaluateAsync<string[]?>("""
                () => [...document.querySelectorAll('.ant-tabs-tab-btn')]
                    .map(el => (el.innerText || '').replace(/\s+/g, ' ').trim())
                    .filter(t => t.length > 0)
                """);
            if (names is null)
            {
                return [];
            }
            HashSet<string> seen = new(StringComparer.Ordinal);
            List<string> result = new(names.Length);
            foreach (string name in names)
            {
                if (seen.Add(name))
                {
                    result.Add(name);
                }
            }
            return result;
        }
        catch
        {
            return [];
        }
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
        string html = await page.ContentAsync();
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (html.Contains("E{\"digest\":", StringComparison.Ordinal))
            {
                break;            // inactive page — nothing to await
            }

            if (NextJsTabParser.Parse(html, tabName: null).Count > 0)
            {
                break;
            }

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
                            {
                                return sib.innerText.trim() || null;
                            }
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

    private static string? Norm(string? s)
    {
        return string.IsNullOrWhiteSpace(s) ? null : System.Net.WebUtility.HtmlDecode(s.Trim());
    }

    private static async Task<bool> TryClickTabAsync(IPage page, string tabName)
    {
        try
        {
            string tabNameJson = System.Text.Json.JsonSerializer.Serialize(tabName);
            bool clicked = await page.EvaluateAsync<bool>($$"""
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
    /// click — the card shape is <see cref="TgMenuDomParser"/>'s.
    /// </summary>
    private static async Task<List<(string, decimal)>> ExtractFromLiveDomAsync(string html, string url)
    {
        IDocument doc = await PageFetcher.ParseHtmlAsync(html, url);
        return [.. TgMenuDomParser.Parse(doc).Select(m => (m.Name, m.Price))];
    }

    /// <summary>The first product card title on screen, or "" when none is.</summary>
    private static async Task<string> FirstCardTitleAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string>("""
                () => document.querySelector('.grid.lg\\:grid-cols-2 [id^="product-card-"][id$="-title"]')
                    ?.innerText?.trim() ?? ''
                """);
        }
        catch (PlaywrightException)
        {
            return "";
        }
    }

    /// <summary>
    /// Harvest restaurant slugs for this chain from the TG sitemap XML and
    /// upsert them as PLATFORM_LOCATIONS rows.
    /// </summary>
    private static async Task HarvestSitemapSlugsAsync(
        ChainConfig chain, SqliteConnection conn, long qsrId, long platformId, string platformSlug,
        IProgress<string>? progress)
    {
        using HttpClient http = new();
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // TaskCanceledException is HttpClient's timeout — previously uncaught,
            // it would have crashed the whole Step 2 run on one slow sitemap fetch.
            progress?.Report($"  ⚠ Failed to fetch sitemap: {ex.Message}");
            return;
        }

        string prefix = $"https://www.tiklagelsin.com/restoran/{platformSlug}/";
        List<string> slugs = [];
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(xml, $@"<loc>\s*{System.Text.RegularExpressions.Regex.Escape(prefix)}([^<?\s]+)"))
        {
            slugs.Add(m.Groups[1].Value.TrimEnd('/'));
        }

        progress?.Report($"  ✓ {slugs.Count} {platformSlug} slugs in sitemap");

        // One transaction for the whole batch instead of an implicit commit
        // (fsync) per slug — turns thousands of round-trips into one. The
        // sitemap is a full census, so this also reactivates returning slugs
        // and soft-deletes the ones the platform delisted (empty census = no-op).
        Repository.PlatformCensusResult census =
            Repository.UpsertPlatformLocationsBulk(conn, qsrId, platformId, slugs);
        int active = CountPlatformLocations(conn, qsrId, platformId);

        progress?.Report(
            $"  ✓ sitemap: {census.Inserted} new, {census.Reactivated} reactivated, "
            + $"{census.Delisted.Count} delisted ({active} active total)");
        foreach (string gone in census.Delisted)
        {
            progress?.Report($"    ✂ \"{gone}\" no longer in the sitemap — soft-deleted");
        }
    }

    private static int CountPlatformLocations(SqliteConnection conn, long qsrId, long platformId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM PLATFORM_LOCATIONS WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat AND ACTIVE = 1;";
        cmd.Parameters.AddWithValue("$qsr", qsrId);
        cmd.Parameters.AddWithValue("$plat", platformId);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
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
            WHERE pl.QSR_ID = $qsr AND pl.PLATFORM_ID = $plat AND pl.ACTIVE = 1
              AND NOT EXISTS (
                  SELECT 1 FROM MENU_SNAPSHOTS ms
                  WHERE ms.PLATFORM_LOCATION_ID = pl.ID AND ms.SCRAPE_RUN_ID = $run);
            """;
        cmd.Parameters.AddWithValue("$qsr", qsrId);
        cmd.Parameters.AddWithValue("$plat", platformId);
        cmd.Parameters.AddWithValue("$run", runId);

        List<(long, string)> result = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        return result;
    }

    private static List<(long Id, string Slug)> LoadFullQueue(SqliteConnection conn, long qsrId, long platformId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT ID, SLUG FROM PLATFORM_LOCATIONS WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat AND ACTIVE = 1;";
        cmd.Parameters.AddWithValue("$qsr", qsrId);
        cmd.Parameters.AddWithValue("$plat", platformId);

        List<(long, string)> result = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        return result;
    }
}
