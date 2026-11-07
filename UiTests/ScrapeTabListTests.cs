using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Ui;
using QsrPriceBenchmarks.Ui.ViewModels;
using Xunit;
using static QsrPriceBenchmarks.UiTests.Dispatching;

namespace QsrPriceBenchmarks.UiTests;

/// <summary>
/// The Run Pipeline tab's tab list, as the maintainer asked for it 2026-10-28: every enabled tab comes ticked,
/// a tab disabled because no location showed it any more is not offered, and once a page shows it again it is
/// back — ticked.
/// </summary>
public sealed class ScrapeTabListTests
{
    private const string Stuck = "The tab list did not reach the expected state";

    private const string Chain = "Burger King";

    [Fact]
    public void Enabled_Tabs_Come_Ticked_And_A_Reenabled_Tab_Comes_Back_Ticked()
    {
        UserDatabaseFile userDatabaseBefore = UserDatabaseFile.State();
        List<(string Name, bool Ticked)> afterDisable = [];
        List<(string Name, bool Ticked)> afterReenable = [];

        using (ThrowawayDatabase throwaway = new(conn =>
        {
            (long qsrId, long platformId) = Ids(conn);
            foreach (string tab in (string[])["Popüler Ürünler", "Menüler", "Kampanyalar"])
            {
                Repository.UpsertDiscoveredScrapeTab(conn, qsrId, platformId, tab);
            }
            // A run that saw no location showing "Kampanyalar".
            Repository.ReconcileScrapeTabs(conn, qsrId, platformId, ["Popüler Ürünler", "Menüler"]);
        }))
        {
            WpfThread.Run(() =>
            {
                MainWindow window = new();
                MainViewModel model = (MainViewModel)window.DataContext;
                model.DbPath = throwaway.Path;
                Task startup = model.InitialiseAsync();
                PumpUntil(() => startup.IsCompleted, Stuck);

                ChainViewModel chain = model.Chains.First(c => c.Name == Chain);
                model.SelectedChain = chain;
                PumpUntil(() => model.SelectedSession!.ScrapeTabs.Count == 2, Stuck);
                afterDisable.AddRange(model.SelectedSession!.ScrapeTabs.Select(t => (t.Name, t.IsSelected)));

                // A later run's page shows the tab again.
                using (SqliteConnection conn = Database.Open(throwaway.Path))
                {
                    (long qsrId, long platformId) = Ids(conn);
                    Repository.UpsertDiscoveredScrapeTab(conn, qsrId, platformId, "Kampanyalar");
                }
                model.SelectedChain = model.Chains.First(c => c.Name != Chain);
                model.SelectedChain = chain;
                PumpUntil(() => model.SelectedSession!.ScrapeTabs.Count == 3, Stuck);
                afterReenable.AddRange(model.SelectedSession!.ScrapeTabs.Select(t => (t.Name, t.IsSelected)));
                window.Close();
            });
        }

        Assert.Equal([("Popüler Ürünler", true), ("Menüler", true)], afterDisable);
        Assert.Contains(("Kampanyalar", true), afterReenable);
        Assert.All(afterReenable, t => Assert.True(t.Ticked, $"'{t.Name}' came unticked"));
        Assert.Equal(userDatabaseBefore, UserDatabaseFile.State());
    }

    private static (long QsrId, long PlatformId) Ids(SqliteConnection conn)
    {
        ChainConfig config = ChainRegistry.All.First(c => c.Name == Chain);
        return (Repository.QsrId(conn, config.Name), Repository.PlatformId(conn, config.PrimaryPlatform));
    }
}
