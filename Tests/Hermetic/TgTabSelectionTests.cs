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

    [Fact(DisplayName = "A disabled tab is never left out, so the page that shows it again gets it scraped")]
    public void DisabledTab_IsNotExcluded()
    {
        using TempDb db = new();
        (long qsrId, long platformId) = Ids(db);
        foreach (string tab in (string[])["Popüler Ürünler", "Kampanyalar"])
        {
            Repository.UpsertDiscoveredScrapeTab(db.Conn, qsrId, platformId, tab);
        }
        Repository.ReconcileScrapeTabs(db.Conn, qsrId, platformId, ["Popüler Ürünler"]);

        // The UI lists only enabled tabs, so its selection can never contain the disabled one.
        (HashSet<string> excluded, HashSet<string> known) =
            TgScraper.ResolveTabs(db.Conn, qsrId, platformId, tabFilter: ["Popüler Ürünler"], progress: null);

        Assert.DoesNotContain("Kampanyalar", excluded);
        Assert.DoesNotContain("Kampanyalar", known); // so seeing it again re-enables it (RecordDiscoveredTabs)
    }

    [Theory(DisplayName = "Unseen tabs are disabled after a fresh run that saw any tab bar, never after a resume")]
    [InlineData(false, 12, true)]
    [InlineData(false, 0, false)]
    [InlineData(true, 12, false)]
    public void ShouldDisableUnseenTabs_Cases(bool resumed, int observedTabCount, bool expected)
    {
        Assert.Equal(expected, TgScraper.ShouldDisableUnseenTabs(resumed, observedTabCount));
    }
}
