using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Ui;
using QsrPriceBenchmarks.Ui.ViewModels;
using Xunit;
using static QsrPriceBenchmarks.UiTests.VisualTree;

namespace QsrPriceBenchmarks.UiTests;

/// <summary>
/// The Scrape Runs grid's filtering and ordering (maintainer, 2026-10-29): newest first; a header click re-sorts
/// and the sort outlives the live reload after every scraped page; Finished sorts by meaning; the Chain and
/// Finished headers carry Excel-style filters, a funnel opening a checkbox per value. The window is shown, so a
/// header is clicked the way a user does.
/// </summary>
public sealed class ScrapeRunsFilterSortTests
{
    [Fact]
    public void Runs_Are_Sorted_And_The_Sort_Survives_A_Reload()
    {
        List<long> byDefault = [], snapshotsAfterReload = [], byFinished = [];

        (long crashed, long running) = ShowRuns((window, model) =>
        {
            byDefault.AddRange(Shown(model));

            // A header click, as a user makes it, then the reload a scraped page triggers.
            Click(window, nameof(ScrapeRunViewModel.SnapshotCount));
            model.NotifyRunsChanged();
            Pump(() => false, TimeSpan.FromSeconds(1));
            snapshotsAfterReload.AddRange(Shown(model));

            Click(window, nameof(ScrapeRunViewModel.FinishedAt));
            byFinished.AddRange(Shown(model));
        });

        Assert.Equal([running, crashed, 3L, 2L, 1L], byDefault);                     // newest first
        // Ascending by snapshot count after the reload: the two 0-snapshot runs tie, then 1, 2 and 3 snapshots.
        Assert.Equal([crashed, running], snapshotsAfterReload.Take(2).Order());
        Assert.Equal([2L, 3L, 1L], snapshotsAfterReload.Skip(2));
        Assert.Equal([running, crashed, 1L, 2L, 3L], byFinished);                     // running, unfinished, by time
    }

    [Fact]
    public void Runs_Are_Filtered_From_The_Header_Funnels()
    {
        List<long> withoutFinished = [], noneTicked = [], arbysOnly = [];
        List<string> chainChoices = [];
        bool funnelPressHandled = false, stateFunnelLit = false;
        bool? allNow = true;

        ShowRuns((window, model) =>
        {
            // The Chain header's funnel: pressing it is the funnel's, never the header's (no re-sort), and it
            // opens a checkbox per chain under "(Select all)".
            ToggleButton chainFunnel = Funnel(window, nameof(ScrapeRunViewModel.QsrName));
            string sortBefore = SortOf(model);
            MouseButtonEventArgs press = new(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            };
            chainFunnel.RaiseEvent(press);
            funnelPressHandled = press.Handled && SortOf(model) == sortBefore;
            chainFunnel.IsChecked = true;
            Pump(() => false, TimeSpan.FromMilliseconds(300));
            chainChoices.AddRange(PopupChoices(chainFunnel));
            chainFunnel.IsChecked = false;

            // The Finished header's filter: untick Finished.
            Option(model.RunsFilter.State, nameof(RunState.Finished)).IsSelected = false;
            withoutFinished.AddRange(Shown(model));
            stateFunnelLit = model.RunsFilter.State.IsActive;
            model.RunsFilter.State.AllSelected = true;

            // The Chain header's filter: "(Select all)" off, then one chain ticked.
            model.RunsFilter.Chain.AllSelected = false;
            noneTicked.AddRange(Shown(model));
            Option(model.RunsFilter.Chain, "Arby's").IsSelected = true;
            arbysOnly.AddRange(Shown(model));
            allNow = model.RunsFilter.Chain.AllSelected;
        });

        Assert.DoesNotContain(1L, withoutFinished);
        Assert.Equal(2, withoutFinished.Count);
        Assert.True(stateFunnelLit, "the Finished funnel must show while it hides rows");
        Assert.Empty(noneTicked);
        Assert.Equal([1L, 2L], arbysOnly.Order());
        Assert.Null(allNow); // "(Select all)" is mixed with one chain of seven ticked
        Assert.True(funnelPressHandled, "a press on the funnel must be the funnel's, not a header click that sorts");
        Assert.Equal(["(Select all)", .. ChainRegistry.All.Select(c => c.Name).Order(StringComparer.CurrentCulture)],
            chainChoices);
    }

    /// <summary>
    /// Seeds a throwaway database with three finished runs (ids 1–3: Arby's ×2, Burger King), a crashed one and a
    /// running one, shows the window on its Scrape Runs tab, and runs <paramref name="act"/> on the WPF thread.
    /// </summary>
    private static (long Crashed, long Running) ShowRuns(Action<MainWindow, MainViewModel> act)
    {
        UserDatabaseFile userDatabaseBefore = UserDatabaseFile.State();
        long crashed = 0, running = 0;
        RunLock? live = null;

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
                Pump(() => Descendants<DataGridColumnHeader>(window).Any(h => h.Column?.SortMemberPath == nameof(ScrapeRunViewModel.SnapshotCount)));
                act(window, model);
                window.Close();
            });
            live?.Dispose();
        }

        Assert.Equal(userDatabaseBefore, UserDatabaseFile.State());
        return (crashed, running);
    }

    private static List<long> Shown(MainViewModel model)
    {
        return [.. model.RunsFilter.View.Cast<ScrapeRunViewModel>().Select(r => r.Id)];
    }

    /// <summary>
    /// Clicks the header of the column bound to <paramref name="property"/>: a header is a button, and a mouse
    /// click reaches the sort through its OnClick, which is called here (its automation peer offers no Invoke).
    /// </summary>
    private static void Click(Window window, string property)
    {
        typeof(ButtonBase).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(Header(window, property), null);
        Pump(() => false, TimeSpan.FromMilliseconds(300));
    }

    private static DataGridColumnHeader Header(Window window, string property)
    {
        return Descendants<DataGridColumnHeader>(window).First(h => h.Column?.SortMemberPath == property);
    }

    /// <summary>The filter funnel in the header of the column bound to <paramref name="property"/>.</summary>
    private static ToggleButton Funnel(Window window, string property)
    {
        return Descendants<ToggleButton>(Header(window, property)).Single();
    }

    /// <summary>The checkbox labels the funnel's open popup shows, in order.</summary>
    private static List<string> PopupChoices(ToggleButton funnel)
    {
        Popup popup = Descendants<Popup>(VisualTreeHelper.GetParent(funnel)).Single();
        return [.. Descendants<CheckBox>(popup.Child).Select(c => c.Content?.ToString() ?? "")];
    }

    private static FilterOptionViewModel Option(ColumnFilterViewModel filter, string value)
    {
        return filter.Options.Single(o => o.Value == value);
    }

    /// <summary>The view's current sort, as text, to tell whether something re-sorted it.</summary>
    private static string SortOf(MainViewModel model)
    {
        return string.Join(",", model.RunsFilter.View.SortDescriptions.Select(s => $"{s.PropertyName} {s.Direction}"));
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
