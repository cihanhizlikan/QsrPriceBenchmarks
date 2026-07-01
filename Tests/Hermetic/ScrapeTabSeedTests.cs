using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// For every chain, the tabs seeded into SCRAPE_TABS by <see cref="Database.Open"/>
/// must equal that chain's configured tab list, in order. This pins the
/// authoritative seed across all chains (not just Burger King) so the new
/// chains' long tab lists are guaranteed to land correctly and stay ordered.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Db")]
public sealed class ScrapeTabSeedTests
{
    public static IEnumerable<object[]> AllChains() =>
        Chains.All.Select(c => new object[] { c.Name });

    [Theory(DisplayName = "Seeded tabs match the chain config, in order")]
    [MemberData(nameof(AllChains))]
    public void SeededTabs_MatchConfig(string name)
    {
        var chain = Chains.All.First(c => c.Name == name);

        using var db = new TempDb();
        var qsrId = Repository.QsrId(db.Conn, chain.Name);
        var platformId = Repository.PlatformId(db.Conn, chain.PrimaryPlatform);

        var seeded = Repository.LoadScrapeTabs(db.Conn, qsrId, platformId);

        Assert.Equal(chain.PrimaryTabs.ToArray(), seeded.ToArray());
    }
}
