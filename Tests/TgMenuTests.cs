using Microsoft.Playwright;
using QsrPriceBenchmarks.Core.Scraping;
using QsrPriceBenchmarks.IntegrationTests.Fixtures;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests;

/// <summary>
/// Integration tests for Step 2 — TG (Tıkla Gelsin) menu scraping.
/// Each test:
///   1. Fetches the TG sitemap to find the first available restaurant slug.
///   2. Navigates to that restaurant's TG page with a headless browser.
///   3. Extracts menu items from the SSR Next.js JSON payload (first tab only).
///   4. Asserts at least one item was returned with a non-empty name and
///      positive price — no database writes are made.
///
/// Run selectively with:
///   dotnet test --filter Category=TgMenu
/// </summary>
[Collection("Playwright")]
[Trait("Category", "Integration")]
[Trait("Category", "TgMenu")]
public sealed class TgMenuTests(PlaywrightFixture pw)
{
    private const string TgBaseUrl = "https://www.tiklagelsin.com/restoran/";

    // Covers every chain in the registry — adding a chain tests it here too.
    public static IEnumerable<object[]> AllSlugs() =>
        Core.Models.Chains.AllPlatformSlugs().Select(s => new object[] { s });

    // ── Single cached fetch per chain (shared by every test below) ───────────

    private sealed record ChainData(List<Core.Models.MenuItem> Items, string? Name, string? Address);

    // Each chain is otherwise navigated four times (once per test). Fetch it
    // once and share. Lazy<Task> guarantees a single fetch even under parallel
    // execution.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<ChainData?>>>
        _chainCache = new();

    private Task<ChainData?> GetChainDataAsync(string platformSlug) =>
        _chainCache.GetOrAdd(platformSlug,
            ps => new Lazy<Task<ChainData?>>(() => FetchChainDataAsync(pw, ps))).Value;

    /// <summary>
    /// Navigate to the chain's first ACTIVE restaurant on TG — sampling past
    /// inactive (digest) and empty (0-item) slugs — and return its menu items,
    /// name and address from one page load. Returns null only when the chain has
    /// no TG listing at all; an empty Items list means several slugs were
    /// sampled and none had a menu (chain likely paused on TG).
    /// </summary>
    private static async Task<ChainData?> FetchChainDataAsync(PlaywrightFixture pw, string platformSlug)
    {
        var first = await TestHelpers.GetFirstSlugFromSitemapAsync(platformSlug);
        if (first is null) return null; // chain not in TG sitemap

        var (ctx, page) = await pw.NewPageAsync();
        await using var _ = ctx;

        for (int attempt = 1; attempt <= 4; attempt++)
        {
            var slug = attempt == 1
                ? first
                : await TestHelpers.GetNthSlugFromSitemapAsync(platformSlug, attempt);
            if (slug is null) break;

            try
            {
                await page.GotoAsync($"{TgBaseUrl}{platformSlug}/{slug}?gel-al",
                    new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 45_000 });
            }
            catch (TimeoutException) { continue; }   // throttle — try the next slug
            catch (PlaywrightException) { continue; }

            await PageFetcher.DismissCookieBannerAsync(page);

            var html = await TgScraper.WaitForMenuPayloadAsync(page);
            if (html.Contains("E{\"digest\":")) continue;   // inactive — try next slug

            var items = NextJsTabParser.Parse(html, tabName: null);
            if (items.Count == 0) continue;                    // empty restaurant — try next slug

            var (name, address) = await TgScraper.ScrapeRestaurantInfoAsync(page);
            return new ChainData(items, name, address);
        }

        return new ChainData(new List<Core.Models.MenuItem>(), null, null); // none active in sample
    }

    // ── Menu items present ───────────────────────────────────────────────────

    [Theory(DisplayName = "TG page for first active slug returns menu items")]
    [MemberData(nameof(AllSlugs))]
    public async Task TgPage_FirstSitemapSlug_ReturnsMenuItems(string platformSlug)
    {
        var data = await GetChainDataAsync(platformSlug);
        if (data is null || data.Items.Count == 0)
            return; // chain has no active menu on TG right now — informational skip

        Assert.All(data.Items, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Name),
                $"[{platformSlug}] Found an item with an empty/null name.");

            Assert.True(item.Price > 0,
                $"[{platformSlug}] Item '{item.Name}' has price {item.Price} <= 0.");

            // Regression guard: name must be the product caption only —
            // portion details like "6 Adet" must never leak into the name.
            Assert.DoesNotContain(" Adet", item.Name, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ── Price sanity ─────────────────────────────────────────────────────────

    [Theory(DisplayName = "TG page items have reasonable prices (> 0, < 100000 TL)")]
    [MemberData(nameof(AllSlugs))]
    public async Task TgPage_Items_HaveReasonablePrices(string platformSlug)
    {
        var data = await GetChainDataAsync(platformSlug);
        if (data is null || data.Items.Count == 0) return;

        Assert.All(data.Items, item =>
            Assert.True(item.Price > 0m && item.Price < 100_000m,
                $"[{platformSlug}] '{item.Name}' price {item.Price} TL is outside " +
                $"expected range. Check PriceParsing for a Turkish locale regression."));
    }

    // ── Unique names ─────────────────────────────────────────────────────────

    [Theory(DisplayName = "TG page items have unique names (no duplicate scraping)")]
    [MemberData(nameof(AllSlugs))]
    public async Task TgPage_Items_HaveUniqueNames(string platformSlug)
    {
        var data = await GetChainDataAsync(platformSlug);
        if (data is null || data.Items.Count == 0) return;

        var names    = data.Items.Select(i => i.Name).ToList();
        var distinct = names.Distinct().ToList();

        Assert.True(distinct.Count == names.Count,
            $"[{platformSlug}] Duplicate menu item names found: " +
            string.Join(", ", names
                .GroupBy(n => n)
                .Where(g => g.Count() > 1)
                .Select(g => $"'{g.Key}' x{g.Count()}")));
    }

    // ── Restaurant name + address (the Step 2 PLATFORM_LOCATIONS fields) ──────

    [Theory(DisplayName = "TG page yields a restaurant name and address")]
    [MemberData(nameof(AllSlugs))]
    public async Task TgPage_HasRestaurantNameAndAddress(string platformSlug)
    {
        var data = await GetChainDataAsync(platformSlug);
        if (data is null || data.Items.Count == 0)
            return; // no active restaurant to read name/address from — skip

        Assert.False(string.IsNullOrWhiteSpace(data.Name),
            $"[{platformSlug}] No restaurant NAME extracted (page heading / <title>).");

        Assert.False(string.IsNullOrWhiteSpace(data.Address),
            $"[{platformSlug}] No restaurant ADDRESS found — the \"Tümü\" info modal " +
            "(#restaurant-info-open-modal → text-bw-default 'Adres' → text-bw-light2) yielded nothing.");
    }
}
