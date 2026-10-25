using QsrPriceBenchmarks.Core.Models;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// Hermetic validation of the chain registry. These run over every entry in
/// <see cref="ChainRegistry.All"/> — including the newer chains (Arby's, Sbarro,
/// Subway, Popeyes, Usta Dönerci, Usta Pideci) — so a typo in a slug or a
/// mismatched Base/Root URL fails the build instead of silently producing an
/// empty or broken scrape. (Menu tabs are not validated here: they are no
/// longer configured anywhere — Step 2 discovers them live per chain.)
/// </summary>
[Trait("Category", "Unit")]
public sealed class ChainConfigTests
{
    public static IEnumerable<object[]> AllChains() =>
        ChainRegistry.All.Select(c => new object[] { c.Name });

    private static ChainConfig Chain(string name) => ChainRegistry.All.First(c => c.Name == name);

    private static bool IsAbsHttpUrl(string? s) =>
        Uri.TryCreate(s, UriKind.Absolute, out Uri? u) && (u.Scheme == "http" || u.Scheme == "https");

    [Theory(DisplayName = "Each chain is well-formed")]
    [MemberData(nameof(AllChains))]
    public void EachChain_IsWellFormed(string name)
    {
        ChainConfig c = Chain(name);

        Assert.False(string.IsNullOrWhiteSpace(c.Name), "Name must not be blank.");
        Assert.True(IsAbsHttpUrl(c.SitemapUrl), $"{name}: SitemapUrl must be an absolute http(s) URL.");

        // Platform slugs: exactly one platform today (Tıkla Gelsin). The seeding
        // resolves a single platform id, so a second platform would currently be
        // mis-seeded — pin the assumption here so adding one fails loudly.
        Assert.Single(c.PlatformSlugs);
        Assert.True(c.PlatformSlugs.ContainsKey("Tıkla Gelsin"),
            $"{name}: the one platform must be 'Tıkla Gelsin'.");
        foreach (string slug in c.PlatformSlugs.Values)
        {
            Assert.Matches("^[a-z0-9-]+$", slug); // URL-path-safe
        }

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
        foreach (string slug in c.PlatformSlugs.Values)
        {
            Assert.Same(c, ChainRegistry.FindByPlatformSlug(slug));
        }
    }

    [Fact(DisplayName = "Chain names and platform slugs are globally unique")]
    public void Names_And_Slugs_AreUnique()
    {
        List<string> names = [.. ChainRegistry.All.Select(c => c.Name)];
        Assert.Equal(names.Count, names.Distinct().Count());

        IReadOnlyList<string> slugs = ChainRegistry.AllPlatformSlugs();
        Assert.Equal(slugs.Count, slugs.Distinct().Count());
    }

    [Fact(DisplayName = "FindByPlatformSlug is case-insensitive and returns null for unknowns")]
    public void FindByPlatformSlug_Behaviour()
    {
        ChainConfig first = ChainRegistry.All.First();
        string slug = first.PlatformSlugs.Values.First();

        Assert.Same(first, ChainRegistry.FindByPlatformSlug(slug.ToUpperInvariant()));
        Assert.Same(first, ChainRegistry.FindByPlatformSlug($"  {slug}  ")); // trimmed
        Assert.Null(ChainRegistry.FindByPlatformSlug("no-such-slug-xyz"));
    }
}
