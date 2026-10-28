using Microsoft.Playwright;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Core.Scraping;
using QsrPriceBenchmarks.IntegrationTests.Fixtures;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests;

/// <summary>
/// Step 2 end to end on one live Tıkla Gelsin page per chain, through the production
/// <see cref="TgScraper.ScrapePageAsync"/>: every tab the page's tab bar shows — the server-rendered first one
/// and every one only React renders after a click — must yield menu items. Nothing is skipped silently: a
/// chain with no readable page in its sample FAILS, because "no data" is exactly how a broken scraper looks.
///
/// Run selectively with: dotnet test --filter Category=TgAllTabs
/// </summary>
[Collection("Playwright")]
[Trait("Category", "Integration")]
[Trait("Category", "TgAllTabs")]
public sealed class TgAllTabsTests(PlaywrightFixture pw)
{
    // How many sitemap listings to try before calling a chain unreadable: closed, out-of-order and throttled
    // pages are normal, and each is skipped for the next.
    private const int SampleSize = 8;

    public static IEnumerable<object[]> AllSlugs() =>
        ChainRegistry.AllPlatformSlugs().Select(s => new object[] { s });

    [Theory(DisplayName = "Every tab of a live TG page yields menu items")]
    [MemberData(nameof(AllSlugs))]
    public async Task EveryTab_YieldsMenuItems(string platformSlug)
    {
        (IBrowserContext ctx, IPage page) = await pw.NewPageAsync();
        await using IBrowserContext _ = ctx;

        List<string> tried = [];
        for (int n = 1; n <= SampleSize; n++)
        {
            string? slug = await TestHelpers.GetNthSlugFromSitemapAsync(platformSlug, n);
            if (slug is null)
            {
                break;
            }

            TgScraper.TgPageData data;
            try
            {
                data = await TgScraper.ScrapePageAsync(page, platformSlug, slug, new HashSet<string>())
                    .WaitAsync(TimeSpan.FromMinutes(4));
            }
            catch (Exception error) when (error is PlaywrightException or TimeoutException)
            {
                tried.Add($"{slug}: {error.GetType().Name}: {error.Message.Split('\n')[0]}");
                continue;
            }

            if (data.Reason is not null || data.PageTabs is not { Count: > 0 })
            {
                tried.Add($"{slug}: {data.Reason ?? "no tab bar"}");
                continue;
            }

            IReadOnlyDictionary<string, List<string>> perTab = data.ItemNamesPerTab!;
            List<string> NamesOf(string tab) => perTab.GetValueOrDefault(tab) ?? [];
            string counts = string.Join(", ", data.PageTabs.Select(t => $"{t}={NamesOf(t).Count}"));

            List<string> empty = [.. data.PageTabs.Where(t => NamesOf(t).Count == 0)];
            Assert.True(empty.Count == 0,
                $"[{platformSlug}/{slug}] {empty.Count} of {data.PageTabs.Count} tab(s) yielded no items: " +
                $"{string.Join(", ", empty)}. Per tab: {counts}");

            // A click that never re-rendered leaves the previous tab's cards on screen, which would read as items.
            List<string> repeats = [.. data.PageTabs.Skip(1).Zip(data.PageTabs)
                .Where(p => NamesOf(p.First).SequenceEqual(NamesOf(p.Second)))
                .Select(p => p.First)];
            Assert.True(repeats.Count == 0,
                $"[{platformSlug}/{slug}] tab(s) showing exactly the previous tab's products, so the click did not " +
                $"render them: {string.Join(", ", repeats)}. Per tab: {counts}");

            Assert.All(data.Items, item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.Name), $"[{platformSlug}/{slug}] an item has no name");
                Assert.True(item.Price is > 0m and < 100_000m,
                    $"[{platformSlug}/{slug}] '{item.Name}' has price {item.Price}");
            });
            return;
        }

        Assert.Fail($"[{platformSlug}] no readable page among the first {SampleSize} sitemap listings: " +
            string.Join("; ", tried));
    }
}
