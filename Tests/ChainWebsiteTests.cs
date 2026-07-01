using Microsoft.Playwright;
using QsrPriceBenchmarks.Core.Scraping;
using QsrPriceBenchmarks.Core.Util;
using QsrPriceBenchmarks.IntegrationTests.Fixtures;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests;

/// <summary>
/// Integration tests for Step 1 — chain-website scraping.
/// Verifies that Playwright + AngleSharp + our parsers can discover province links
/// and extract at least one restaurant card with a non-null name, without any
/// database writes.
///
/// Run selectively with:
///   dotnet test --filter Category=ChainWebsite
/// </summary>
[Collection("Playwright")]
[Trait("Category", "Integration")]
[Trait("Category", "ChainWebsite")]
public sealed class ChainWebsiteTests(PlaywrightFixture pw)
{
    // Covers every chain in the registry — add a chain and it's tested here
    // automatically (no InlineData to keep in sync).
    public static IEnumerable<object[]> AllSlugs() =>
        Core.Models.Chains.AllPlatformSlugs().Select(s => new object[] { s });

    // ── Province-link discovery ───────────────────────────────────────────────────

    [Theory(DisplayName = "Root page yields at least one province link")]
    [MemberData(nameof(AllSlugs))]
    public async Task RootPage_YieldsAtLeastOneProvinceLink(string platformSlug)
    {
        var chain = TestHelpers.RequireChain(platformSlug);
        Assert.NotNull(chain.RootUrl);

        var (ctx, page) = await pw.NewPageAsync();
        await using var _ = ctx;

        var html      = await FetchWithScrollAsync(page, chain.RootUrl!, chain.ContentSelector);
        var doc       = await PageFetcher.ParseHtmlAsync(html, chain.RootUrl!);
        var provinceLinks = UrlUtils.ParseDepthLinks(doc, chain.RootUrl!, depthOffset: 1);

        Assert.True(provinceLinks.Count > 0,
            $"[{chain.Name}] Expected at least one province link under {chain.RootUrl}, got 0.");

        Assert.All(provinceLinks, url =>
        {
            Assert.StartsWith("https://", url, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(new Uri(chain.RootUrl!).Host, url, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ── Restaurant card extraction ────────────────────────────────────────────

    [Theory(DisplayName = "First restaurant listing page yields a card with a name")]
    [MemberData(nameof(AllSlugs))]
    public async Task FirstRestaurantListingPage_YieldsCardWithName(string platformSlug)
    {
        var chain = TestHelpers.RequireChain(platformSlug);
        Assert.NotNull(chain.RootUrl);

        var (ctx, page) = await pw.NewPageAsync();
        await using var _ = ctx;

        // ── Root → first province link ────────────────────────────────────────────
        var rootHtml  = await FetchWithScrollAsync(page, chain.RootUrl!, chain.ContentSelector);
        var rootDoc   = await PageFetcher.ParseHtmlAsync(rootHtml, chain.RootUrl!);
        var provinceLinks = UrlUtils.ParseDepthLinks(rootDoc, chain.RootUrl!, depthOffset: 1);

        Assert.True(provinceLinks.Count > 0,
            $"[{chain.Name}] No province links found on root page.");

        // ── Walk the hierarchy until we find a named card.
        //    Chain sites vary: some list restaurants directly at depth 1,
        //    others go root→province→district→restaurant (depth 3 after root).
        //    We try up to 3 hops before giving up.
        var namedCard = await FindFirstNamedCardAsync(page, chain.RootUrl!, provinceLinks[0], maxDepth: 3);

        Assert.True(namedCard is not null,
            $"[{chain.Name}] No restaurant card with a non-null Name found " +
            $"within 3 hops of the first province link '{provinceLinks[0]}'. " +
            $"The site markup may have changed — check RestaurantCardParser strategies.");

        // Verify the card URL is deeper than root (it's a real restaurant page).
        var rootParts = UrlUtils.SplitPath(new Uri(chain.RootUrl!).AbsolutePath);
        var cardParts = UrlUtils.SplitPath(new Uri(namedCard!.Url).AbsolutePath);
        Assert.True(cardParts.Count > rootParts.Count,
            $"[{chain.Name}] Card URL '{namedCard.Url}' is not deeper than root — " +
            $"looks like a navigation link was returned instead of a restaurant.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Navigate to <paramref name="url"/> and scroll the page fully before
    /// returning the HTML. Many chain sites use lazy-loading or JS rendering —
    /// scrolling triggers the content to appear in <c>ContentAsync()</c>.
    /// </summary>
    private static async Task<string> FetchWithScrollAsync(
        IPage page, string url, string? waitForSelector = null)
    {
        await page.GotoAsync(url, new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout   = 30_000,
        });

        await PageFetcher.DismissCookieBannerAsync(page);

        // If a known "loaded" selector was provided, wait for it.
        if (waitForSelector is not null)
        {
            try
            {
                await page.WaitForSelectorAsync(waitForSelector,
                    new PageWaitForSelectorOptions { Timeout = 6_000 });
            }
            catch (TimeoutException) { /* fall through */ }
        }

        // Scroll in steps to trigger lazy-loaded restaurant cards.
        for (int i = 0; i < 5; i++)
        {
            await page.EvaluateAsync("window.scrollBy(0, window.innerHeight)");
            await page.WaitForTimeoutAsync(300);
        }
        await page.EvaluateAsync("window.scrollTo(0, 0)");
        await page.WaitForTimeoutAsync(400);

        return await page.ContentAsync();
    }

    /// <summary>
    /// Breadth-first walk through the chain-website hierarchy starting from
    /// <paramref name="startUrl"/> until a <see cref="Core.Models.RestaurantCard"/>
    /// with a non-null <c>Name</c> is found or <paramref name="maxDepth"/> hops
    /// are exhausted.
    /// </summary>
    private async Task<Core.Models.RestaurantCard?> FindFirstNamedCardAsync(
        IPage page, string rootUrl, string startUrl, int maxDepth)
    {
        var queue = new Queue<(string Url, int Depth)>();
        queue.Enqueue((startUrl, 1));
        var visited = new HashSet<string> { startUrl };

        while (queue.Count > 0)
        {
            var (url, depth) = queue.Dequeue();

            string html;
            try
            {
                html = await FetchWithScrollAsync(page, url);
            }
            catch
            {
                continue;
            }

            var doc   = await PageFetcher.ParseHtmlAsync(html, url);
            var cards = RestaurantCardParser.Parse(doc, url);

            // A "real" restaurant card has a name AND sits at URL depth > root.
            var rootParts = UrlUtils.SplitPath(new Uri(rootUrl).AbsolutePath);
            var named = cards.FirstOrDefault(c =>
                c.Name is not null &&
                UrlUtils.SplitPath(new Uri(c.Url).AbsolutePath).Count > rootParts.Count + 1);

            if (named is not null)
                return named;

            // Enqueue sub-links one hop deeper if we haven't reached maxDepth.
            if (depth < maxDepth)
            {
                var subLinks = UrlUtils.ParseDepthLinks(doc, url, depthOffset: 1);
                foreach (var sub in subLinks.Take(3)) // limit breadth for test speed
                {
                    if (visited.Add(sub))
                        queue.Enqueue((sub, depth + 1));
                }
            }
        }

        return null;
    }
}
