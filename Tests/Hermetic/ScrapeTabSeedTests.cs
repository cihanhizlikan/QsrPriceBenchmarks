using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// Tabs are never hardcoded: for every chain, a freshly created database has an
/// EMPTY tab list. The list is populated exclusively by Step 2's live discovery
/// (and pruned by its soft-delete reconciliation) — pinning this prevents any
/// hardcoded tab snapshot from creeping back into the seed.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Db")]
public sealed class ScrapeTabSeedTests
{
    public static IEnumerable<object[]> AllChains() =>
        Chains.All.Select(c => new object[] { c.Name });

    [Theory(DisplayName = "Fresh database seeds no tabs — discovery is the only source")]
    [MemberData(nameof(AllChains))]
    public void FreshDatabase_HasNoSeededTabs(string name)
    {
        ChainConfig chain = Chains.All.First(c => c.Name == name);

        using TempDb db = new();
        long qsrId = Repository.QsrId(db.Conn, chain.Name);
        long platformId = Repository.PlatformId(db.Conn, chain.PrimaryPlatform);

        Assert.Empty(Repository.LoadScrapeTabs(db.Conn, qsrId, platformId));

        // And the discovery path is what populates it.
        Repository.UpsertDiscoveredScrapeTab(db.Conn, qsrId, platformId, "Popüler Ürünler");
        Assert.Equal(new[] { "Popüler Ürünler" },
            Repository.LoadScrapeTabs(db.Conn, qsrId, platformId).ToArray());
    }
}
