using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Services;
using QsrPriceBenchmarks.Ui;
using QsrPriceBenchmarks.Ui.ViewModels;
using Xunit;
using static QsrPriceBenchmarks.UiTests.Dispatching;

namespace QsrPriceBenchmarks.UiTests;

/// <summary>
/// The maintainer's decision, 2026-11-13 (WP-U8): with Windows' "Show animations" off, nothing on the window moves,
/// and each cue holds its end state instead of disappearing. Read with a chain running, on the two tabs that
/// animate: the Run Pipeline tab (the header's bars and the progress fill) and the Locations tab (the markers).
/// A progress bar's own value is left out: its glide is kept when animations are off, as Yalbuz keeps it.
/// </summary>
public sealed class MotionTests
{
    private const string Stuck = "The window did not reach the running state";

    [Fact]
    public void With_Animations_Off_Nothing_Moves_And_The_Logo_Stays()
    {
        Watched off = Watch(motion: false);

        Assert.True(off.Running, "the check must run with a chain running, or the bars and the fill are not on screen");
        Assert.Empty(off.Moving);
        Assert.True(off.LogoVisible, "with animations off the logo is the running cue's end state, and must stay");
    }

    [Fact]
    public void With_Animations_On_The_Window_Moves()
    {
        // The instrument: the same walk must find the animations when they are allowed, or an empty list above
        // proves nothing.
        Watched on = Watch(motion: true);

        Assert.True(on.Running, Stuck);
        Assert.Contains(on.Moving, m => m.StartsWith("RunPipelineTab", StringComparison.Ordinal));
        Assert.Contains(on.Moving, m => m.StartsWith("LocationsTab", StringComparison.Ordinal));
        Assert.False(on.LogoVisible, "with animations on, the bars replace the logo while a chain runs");
    }

    private sealed record Watched(bool Running, List<string> Moving, bool LogoVisible);

    private static Watched Watch(bool motion)
    {
        bool running = false, logoVisible = false;
        List<string> moving = [];

        using (ThrowawayDatabase throwaway = new(Seed))
        {
            WpfThread.Run(() =>
            {
                bool before = Motion.Enabled;
                Motion.Enabled = motion;
                try
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
                    model.SelectedChain = model.Chains.First(c => c.Name == "Burger King");
                    model.RunPipeline = async call =>
                    {
                        call.Progress.Report(new StepProgress("Scraping prices", 12, 177));
                        await Task.Delay(Timeout.Infinite, call.Token);
                    };
                    window.Show();
                    PumpUntil(() => model.MapCells.Count > 0, Stuck);

                    model.RunCommand.Execute(null);
                    PumpUntil(() => model.SelectedSession!.ProgressVisible, Stuck);
                    running = model.IsAnyRunning;

                    foreach (string tab in (string[])["RunPipelineTab", "LocationsTab"])
                    {
                        TabItem item = window.FindName(tab) as TabItem
                            ?? throw new InvalidOperationException($"MainWindow has no TabItem named {tab}.");
                        item.IsSelected = true;
                        PumpFor(TimeSpan.FromMilliseconds(600));
                        moving.AddRange(VisualTree.Descendants<UIElement>(window)
                            .Where(e => e.IsVisible && e is not RangeBase && Moves(e))
                            .Select(e => $"{tab}: {e.GetType().Name} {(e as FrameworkElement)?.Name}"));
                    }

                    object logo = window.FindResource("AppLogo");
                    logoVisible = VisualTree.Descendants<Image>(window).Any(i => i.Source == logo && i.IsVisible);

                    model.CancelAllRuns();
                    PumpUntil(() => !model.IsAnyRunning, Stuck);
                    window.Close();
                }
                finally
                {
                    Motion.Enabled = before;
                }
            });
        }

        return new Watched(running, moving, logoVisible);
    }

    /// <summary>
    /// An element moves when one of its own properties is animated, or its render transform is — the header's bars
    /// and the markers' ring animate a ScaleTransform, which the element's own flag does not report.
    /// </summary>
    private static bool Moves(UIElement element)
    {
        return element.HasAnimatedProperties
            || (element.RenderTransform is Animatable transform && transform.HasAnimatedProperties);
    }

    /// <summary>Locations in three provinces, so the map has markers with rings.</summary>
    private static void Seed(SqliteConnection conn)
    {
        long qsrId = Repository.QsrId(conn, "Burger King");
        Repository.UpsertLocation(conn, "istanbul-avrupa", "besiktas", "bk-besiktas", qsrId);
        Repository.UpsertLocation(conn, "ankara", "cankaya", "bk-cankaya", qsrId);
        Repository.UpsertLocation(conn, "izmir", "konak", "bk-konak", qsrId);
    }
}
