using System.Text.RegularExpressions;
using QsrPriceBenchmarks.Core.Models;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// Hermetic validation of the chain registry. These run over every entry in
/// <see cref="Chains.All"/> — including the newer chains (Arby's, Sbarro,
/// Subway, Popeyes, Usta Dönerci, Usta Pideci) — so a typo in a slug, a duplicate
/// or stray-whitespace tab name, or a mismatched Base/Root URL fails the build
/// instead of silently producing an empty or broken scrape.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ChainConfigTests
{
    public static IEnumerable<object[]> AllChains() =>
        Chains.All.Select(c => new object[] { c.Name });

    private static ChainConfig Chain(string name) => Chains.All.First(c => c.Name == name);

    private static bool IsAbsHttpUrl(string? s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");

    [Theory(DisplayName = "Each chain is well-formed")]
    [MemberData(nameof(AllChains))]
    public void EachChain_IsWellFormed(string name)
    {
        var c = Chain(name);

        Assert.False(string.IsNullOrWhiteSpace(c.Name), "Name must not be blank.");
        Assert.True(IsAbsHttpUrl(c.SitemapUrl), $"{name}: SitemapUrl must be an absolute http(s) URL.");

        // Platform slugs: exactly one platform today (Tıkla Gelsin). The seeding
        // resolves a single platform id, so a second platform would currently be
        // mis-seeded — pin the assumption here so adding one fails loudly.
        Assert.Single(c.PlatformSlugs);
        Assert.True(c.PlatformSlugs.ContainsKey("Tıkla Gelsin"),
            $"{name}: the one platform must be 'Tıkla Gelsin'.");
        foreach (var slug in c.PlatformSlugs.Values)
            Assert.Matches("^[a-z0-9-]+$", slug); // URL-path-safe

        // Tab map keys must be a subset of the platforms the chain is on.
        foreach (var platform in c.ScrapeTabs.Keys)
            Assert.Contains(platform, c.PlatformSlugs.Keys);

        // Primary tabs: present, non-blank, trimmed, no duplicates.
        var tabs = c.PrimaryTabs;
        Assert.NotEmpty(tabs);
        foreach (var t in tabs)
        {
            Assert.False(string.IsNullOrWhiteSpace(t), $"{name}: a tab name is blank.");
            Assert.Equal(t, t.Trim()); // no stray leading/trailing whitespace
        }
        Assert.Equal(tabs.Count, tabs.Distinct().Count()); // no duplicate tab names

        // Base/Root URL pairing: both null (TG-only) or both set and consistent.
        if (c.BaseUrl is null || c.RootUrl is null)
        {
            Assert.Null(c.BaseUrl);
            Assert.Null(c.RootUrl);
        }
        else
        {
            Assert.True(IsAbsHttpUrl(c.BaseUrl), $"{name}: BaseUrl must be an absolute http(s) URL.");
            Assert.True(IsAbsHttpUrl(c.RootUrl), $"{name}: RootUrl must be an absolute http(s) URL.");
            // RootUrl is BaseUrl without the trailing slash (used as the crawl root).
            Assert.Equal(c.BaseUrl.TrimEnd('/'), c.RootUrl);
        }

        // Each slug resolves back to this chain.
        foreach (var slug in c.PlatformSlugs.Values)
            Assert.Same(c, Chains.FindByPlatformSlug(slug));
    }

    [Fact(DisplayName = "First tab is the page's SSR default (Popüler Ürünler, except Sbarro)")]
    public void FirstTab_IsSsrDefault()
    {
        // tabs[0] is read from the default server-rendered payload (not clicked),
        // so it must be whatever the page shows first. Sbarro is the documented
        // exception whose page leads with a campaigns tab.
        foreach (var c in Chains.All)
        {
            var expectedFirst = c.Name == "Sbarro" ? "Fırsat Kampanyaları" : "Popüler Ürünler";
            Assert.Equal(expectedFirst, c.PrimaryTabs[0]);
        }
    }

    [Fact(DisplayName = "Chain names and platform slugs are globally unique")]
    public void Names_And_Slugs_AreUnique()
    {
        var names = Chains.All.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());

        var slugs = Chains.AllPlatformSlugs();
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
    }

    [Fact(DisplayName = "FindByPlatformSlug is case-insensitive and returns null for unknowns")]
    public void FindByPlatformSlug_Behaviour()
    {
        var first = Chains.All.First();
        var slug = first.PlatformSlugs.Values.First();

        Assert.Same(first, Chains.FindByPlatformSlug(slug.ToUpperInvariant()));
        Assert.Same(first, Chains.FindByPlatformSlug($"  {slug}  ")); // trimmed
        Assert.Null(Chains.FindByPlatformSlug("no-such-slug-xyz"));
    }
}
