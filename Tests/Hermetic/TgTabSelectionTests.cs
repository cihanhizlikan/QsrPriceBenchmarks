using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Core.Scraping;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// Which tabs a Step 2 run leaves out. The UI passes the ticked known tabs; a fresh database knows none, so its
/// selection is empty — and reading that as "scrape these tabs" made the first run of every chain record no
/// prices at all (measured on the maintainer's database 2026-10-28: an Arby's run over 177 locations, 12 tabs
/// discovered, 0 snapshots).
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Db")]
public sealed class TgTabSelectionTests
{
    private static (long QsrId, long PlatformId) Ids(TempDb db)
    {
        ChainConfig chain = ChainRegistry.All[0];
        return (Repository.QsrId(db.Conn, chain.Name), Repository.PlatformId(db.Conn, chain.PrimaryPlatform));
    }

    [Fact(DisplayName = "A fresh database with an empty UI selection leaves no tab out")]
    public void FreshDatabase_EmptySelection_ExcludesNothing()
    {
        using TempDb db = new();
        (long qsrId, long platformId) = Ids(db);

        (HashSet<string> excluded, HashSet<string> known) =
            TgScraper.ResolveTabs(db.Conn, qsrId, platformId, tabFilter: [], progress: null);

        Assert.Empty(excluded);
        Assert.Empty(known);
    }

    [Fact(DisplayName = "Only the known tabs the user unticked are left out")]
    public void UntickedKnownTabs_AreTheOnlyExclusions()
    {
        using TempDb db = new();
        (long qsrId, long platformId) = Ids(db);
        foreach (string tab in (string[])["Popüler Ürünler", "Menüler", "İçecekler"])
        {
            Repository.UpsertDiscoveredScrapeTab(db.Conn, qsrId, platformId, tab);
        }

        (HashSet<string> excluded, _) =
            TgScraper.ResolveTabs(db.Conn, qsrId, platformId, tabFilter: ["Popüler Ürünler", "İçecekler"], progress: null);

        Assert.Equal(["Menüler"], excluded);
    }

    [Fact(DisplayName = "No selection at all (the CLI) leaves no tab out")]
    public void NoSelection_ExcludesNothing()
    {
        using TempDb db = new();
        (long qsrId, long platformId) = Ids(db);
        Repository.UpsertDiscoveredScrapeTab(db.Conn, qsrId, platformId, "Menüler");

        (HashSet<string> excluded, _) =
            TgScraper.ResolveTabs(db.Conn, qsrId, platformId, tabFilter: null, progress: null);

        Assert.Empty(excluded);
    }
}
