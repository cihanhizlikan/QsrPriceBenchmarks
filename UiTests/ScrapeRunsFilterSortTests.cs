using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Ui;
using QsrPriceBenchmarks.Ui.ViewModels;
using Xunit;

namespace QsrPriceBenchmarks.UiTests;

/// <summary>
/// The Scrape Runs grid's filtering and ordering (maintainer, 2026-10-29): newest first; a header click re-sorts
/// and the sort outlives the live reload after every scraped page; Finished sorts by meaning; the state switches
/// and "only the selected chain" filter the rows. The window is shown, so a header is clicked the way a user does.
/// </summary>
public sealed class ScrapeRunsFilterSortTests
{
    [Fact]
    public void Runs_Are_Filtered_And_Sorted_And_The_Sort_Survives_A_Reload()
    {
        UserDatabaseFile userDatabaseBefore = UserDatabaseFile.State();
        long crashed = 0, running = 0;
        RunLock? live = null;
        List<long> byDefault = [], snapshotsAfterReload = [], byFinished = [], withoutFinished = [], arbysOnly = [];
        List<long> burgerKingOnly = [];

        using (ThrowawayDatabase throwaway = new(conn =>
        {
            Finished(conn, "Arby's", snapshots: 3);
            Finished(conn, "Arby's", snapshots: 1);
            Finished(conn, "Burger King", snapshots: 2);
            using (RunLock c = Repository.StartScrapeRun(conn, Qsr(conn, "Popeyes")))
            {
                crashed = c.RunId; // unfinished, its lock released: what a killed process leaves
            }
            live = Repository.StartScrapeRun(conn, Qsr(conn, "Sbarro"));
            running = live.RunId;
        }))
        {
            WpfThread.Run(() =>
            {
                MainWindow window = new()
                {
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = 0,
                    Top = 0,
                };
                MainViewModel model = (MainViewModel)window.DataContext;
                model.DbPath = throwaway.Path;
                window.Show();
                Pump(() => model.Runs.Count == 5);
                ((TabItem)window.FindName("ScrapeRunsTab")).IsSelected = true;
                Pump(() => Descendants<DataGridColumnHeader>(window).Any(h => Equals(h.Content, "Snapshots")));

                byDefault.AddRange(Shown(model));

                // A header click, as a user makes it, then the reload a scraped page triggers.
                Click(window, "Snapshots");
                model.NotifyRunsChanged();
                Pump(() => false, TimeSpan.FromSeconds(1));
                snapshotsAfterReload.AddRange(Shown(model));

                Click(window, "Finished");
                byFinished.AddRange(Shown(model));

                model.RunsFilter.ShowFinished = false;
                withoutFinished.AddRange(Shown(model));
                model.RunsFilter.ShowFinished = true;

                model.RunsFilter.OnlySelectedChain = true;
                model.SelectedChain = model.Chains.First(c => c.Name == "Arby's");
                arbysOnly.AddRange(Shown(model));
                model.SelectedChain = model.Chains.First(c => c.Name == "Burger King");
                burgerKingOnly.AddRange(Shown(model));

                window.Close();
            });
            live?.Dispose();
        }

        Assert.Equal([running, crashed, 3L, 2L, 1L], byDefault);                     // newest first
        // Ascending by snapshot count after the reload: the two 0-snapshot runs tie, then 1, 2 and 3 snapshots.
        Assert.Equal([crashed, running], snapshotsAfterReload.Take(2).Order());
        Assert.Equal([2L, 3L, 1L], snapshotsAfterReload.Skip(2));
        Assert.Equal([running, crashed, 1L, 2L, 3L], byFinished);                     // running, unfinished, by time
        Assert.DoesNotContain(1L, withoutFinished);
        Assert.Equal(2, withoutFinished.Count);
        Assert.Equal([1L, 2L], arbysOnly.Order());
        Assert.Equal([3L], burgerKingOnly);                                           // follows the sidebar
        Assert.Equal(userDatabaseBefore, UserDatabaseFile.State());
    }

    private static List<long> Shown(MainViewModel model)
    {
        return [.. model.RunsFilter.View.Cast<ScrapeRunViewModel>().Select(r => r.Id)];
    }

    /// <summary>
    /// Clicks a column header: a header is a button, and a mouse click reaches the sort through its OnClick, which
    /// is called here (its automation peer offers no Invoke).
    /// </summary>
    private static void Click(Window window, string header)
    {
        DataGridColumnHeader target = Descendants<DataGridColumnHeader>(window).First(h => Equals(h.Content, header));
        typeof(ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(target, null);
        Pump(() => false, TimeSpan.FromMilliseconds(300));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }
            foreach (T deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>A finished run of <paramref name="chain"/> with <paramref name="snapshots"/> prices recorded.</summary>
    private static void Finished(SqliteConnection conn, string chain, int snapshots)
    {
        long qsr = Qsr(conn, chain);
        using RunLock run = Repository.StartScrapeRun(conn, qsr);
        long platformId = Repository.PlatformId(conn, ChainRegistry.All.First(c => c.Name == chain).PrimaryPlatform);
        using SqliteCommand insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO PLATFORM_LOCATIONS (QSR_ID, PLATFORM_ID, SLUG, NAME) VALUES ($q, $p, $slug, 'x');
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$q", qsr);
        insert.Parameters.AddWithValue("$p", platformId);
        insert.Parameters.AddWithValue("$slug", $"pl-{run.RunId}");
        long location = Convert.ToInt64(insert.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        for (int i = 0; i < snapshots; i++)
        {
            Repository.InsertSnapshot(conn, run.RunId, location,
                Repository.GetOrCreateMenuItem(conn, qsr, $"item {i}"), 100m + i);
        }
        Thread.Sleep(1100); // FINISHED_AT has one-second resolution: distinct finish times order the runs
        Repository.FinishScrapeRun(conn, run.RunId);
    }

    private static long Qsr(SqliteConnection conn, string chain)
    {
        return Repository.QsrId(conn, chain);
    }

    /// <summary>Runs the dispatcher until <paramref name="done"/> holds, or for <paramref name="limit"/> at most.</summary>
    private static void Pump(Func<bool> done, TimeSpan? limit = null)
    {
        TimeSpan bound = limit ?? TimeSpan.FromSeconds(20);
        long started = Stopwatch.GetTimestamp();
        while (!done())
        {
            if (Stopwatch.GetElapsedTime(started) > bound)
            {
                if (limit is not null)
                {
                    return; // a fixed settling time, not a wait for something
                }
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
