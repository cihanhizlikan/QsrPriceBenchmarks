using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Ui;
using QsrPriceBenchmarks.Ui.ViewModels;
using Xunit;

namespace QsrPriceBenchmarks.UiTests;

/// <summary>
/// The maintainer's defect, 2026-10-29: the Scrape Runs tab did not update during scraping — it re-read the
/// runs only when opened and when the whole pipeline ended. Step 2 now signals as it starts its run and records
/// each page; with the tab open, the grid shows the new run and its growing snapshot count, and keeps the row
/// the user selected.
/// </summary>
public sealed class ScrapeRunsLiveRefreshTests
{
    private const string Chain = "Burger King";

    [Fact]
    public void The_Open_Scrape_Runs_Tab_Shows_A_Run_In_Progress_And_Keeps_The_Selection()
    {
        UserDatabaseFile userDatabaseBefore = UserDatabaseFile.State();
        long earlierRun = 0;
        bool tabFlagSet = false;
        long? selectedAfter = null;
        int? liveSnapshots = null;
        bool? liveErrored = null;

        using (ThrowawayDatabase throwaway = new(conn =>
        {
            using RunLock earlier = Repository.StartScrapeRun(conn, Ids(conn).QsrId);
            earlierRun = earlier.RunId;
            Repository.FinishScrapeRun(conn, earlierRun);
        }))
        {
            WpfThread.Run(() =>
            {
                MainWindow window = new();
                MainViewModel model = (MainViewModel)window.DataContext;
                model.DbPath = throwaway.Path;
                Task startup = model.InitialiseAsync();
                PumpUntil(() => startup.IsCompleted);

                ((TabItem)window.FindName("ScrapeRunsTab")).IsSelected = true;
                tabFlagSet = model.ScrapeRunsTabActive;
                PumpUntil(() => model.Runs.Count == 1);
                model.SelectedRun = model.Runs[0];

                // What Step 2 does as a run starts and records a page, then the signal it sends.
                RunLock live;
                long liveRun;
                using (SqliteConnection conn = Database.Open(throwaway.Path))
                {
                    (long qsrId, long platformId) = Ids(conn);
                    live = Repository.StartScrapeRun(conn, qsrId);
                    liveRun = live.RunId;
                    long location = PlatformLocation(conn, qsrId, platformId);
                    Repository.InsertSnapshot(conn, liveRun, location,
                        Repository.GetOrCreateMenuItem(conn, qsrId, "Whopper®"), 340m);
                }
                model.NotifyRunsChanged();

                PumpUntil(() => model.Runs.Count == 2);
                liveSnapshots = model.Runs.FirstOrDefault(r => r.Id == liveRun)?.SnapshotCount;
                liveErrored = model.Runs.FirstOrDefault(r => r.Id == liveRun)?.IsErrored;
                live.Dispose();
                selectedAfter = model.SelectedRun?.Id;
                window.Close();
            });
        }

        Assert.True(tabFlagSet, "opening the Scrape Runs tab must mark it active, or no live reload ever runs");
        Assert.Equal(1, liveSnapshots);
        Assert.False(liveErrored, "a run being scraped must not offer Resume as if it had crashed");
        Assert.Equal(earlierRun, selectedAfter);
        Assert.Equal(userDatabaseBefore, UserDatabaseFile.State());
    }

    private static (long QsrId, long PlatformId) Ids(SqliteConnection conn)
    {
        ChainConfig config = ChainRegistry.All.First(c => c.Name == Chain);
        return (Repository.QsrId(conn, config.Name), Repository.PlatformId(conn, config.PrimaryPlatform));
    }

    private static long PlatformLocation(SqliteConnection conn, long qsrId, long platformId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PLATFORM_LOCATIONS (QSR_ID, PLATFORM_ID, SLUG, NAME)
            VALUES ($qsr, $plat, 'zulufluhan-mah-s-4fc92462', 'Burger King (Zülüflühan Mah.)');
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$qsr", qsrId);
        cmd.Parameters.AddWithValue("$plat", platformId);
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Runs the dispatcher until <paramref name="done"/> holds; bounded so a grid that never fills fails.</summary>
    private static void PumpUntil(Func<bool> done)
    {
        long started = Stopwatch.GetTimestamp();
        while (!done())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(20))
            {
                throw new TimeoutException("The Scrape Runs grid did not reach the expected state within 20 s.");
            }
            DispatcherFrame frame = new();
            DispatcherTimer tick = new(TimeSpan.FromMilliseconds(25), DispatcherPriority.Background,
                (_, _) => { frame.Continue = false; }, Dispatcher.CurrentDispatcher);
            tick.Start();
            Dispatcher.PushFrame(frame);
            tick.Stop();
        }
    }
}
