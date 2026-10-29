using System.Diagnostics;
using System.Windows.Threading;
using QsrPriceBenchmarks.Ui;
using QsrPriceBenchmarks.Ui.ViewModels;
using Xunit;

namespace QsrPriceBenchmarks.UiTests;

/// <summary>
/// The maintainer's request, 2026-10-29 (plan WP-C2): switch chains mid-scrape to start, watch or cancel other
/// scrapes, each chain with its own log and progress. The pipeline is stood in by a run that logs, reports progress
/// and waits for its cancellation — the window, sessions and commands are the real ones.
/// </summary>
public sealed class ChainSessionsTests
{
    [Fact]
    public void Two_Chains_Run_At_Once_Each_With_Its_Own_Log_And_Cancel()
    {
        UserDatabaseFile userDatabaseBefore = UserDatabaseFile.State();
        string? aStatusWhileBothRun = null, aStatusAfter = null, bStatusAfter = null;
        bool bothRunning = false, bRunningAfterACancelled = false, anyRunningAtEnd = true;
        List<string> aLog = [], bLog = [];

        using (ThrowawayDatabase throwaway = new())
        {
            WpfThread.Run(() =>
            {
                MainWindow window = new();
                MainViewModel model = (MainViewModel)window.DataContext;
                model.DbPath = throwaway.Path;
                model.RunPipeline = async call =>
                {
                    call.Log.Report($"started {call.Options.Chain.Name}");
                    call.Progress.Report(new Core.Services.StepProgress("Scraping prices", 3, 10));
                    await Task.Delay(Timeout.Infinite, call.Token);
                };
                Task startup = model.InitialiseAsync();
                PumpUntil(() => startup.IsCompleted);

                ChainViewModel a = model.Chains[0], b = model.Chains[1];
                model.SelectedChain = a;
                model.RunCommand.Execute(null);
                PumpUntil(() => a.Session.ProgressTotal == 10);

                // The chain list is not locked: another chain can be chosen and started while A runs.
                model.SelectedChain = b;
                Assert.True(model.RunCommand.CanExecute(null), "Run must be available for an idle chain while another runs");
                model.RunCommand.Execute(null);
                PumpUntil(() => b.Session.ProgressTotal == 10);
                bothRunning = a.Session.IsRunning && b.Session.IsRunning && model.IsAnyRunning;
                aStatusWhileBothRun = a.Session.StatusText;

                // Cancel acts on the chain on screen only.
                model.SelectedChain = a;
                model.CancelCommand.Execute(null);
                PumpUntil(() => !a.Session.IsRunning);
                bRunningAfterACancelled = b.Session.IsRunning;
                aStatusAfter = a.Session.StatusText;

                model.SelectedChain = b;
                model.CancelCommand.Execute(null);
                PumpUntil(() => !b.Session.IsRunning);
                bStatusAfter = b.Session.StatusText;
                anyRunningAtEnd = model.IsAnyRunning;
                aLog.AddRange(a.Session.LogLines);
                bLog.AddRange(b.Session.LogLines);
                window.Close();
            });
        }

        Assert.True(bothRunning, "both chains must run at once");
        Assert.Equal("Scraping prices  3 / 10", aStatusWhileBothRun);
        Assert.True(bRunningAfterACancelled, "cancelling one chain must leave the other running");
        Assert.Equal("Cancelled", aStatusAfter);
        Assert.Equal("Cancelled", bStatusAfter);
        Assert.False(anyRunningAtEnd);
        Assert.Contains(aLog, l => l.Contains("started", StringComparison.Ordinal));
        Assert.Single(aLog, l => l.StartsWith("started", StringComparison.Ordinal));
        Assert.Single(bLog, l => l.StartsWith("started", StringComparison.Ordinal));
        Assert.NotEqual(aLog.First(l => l.StartsWith("started", StringComparison.Ordinal)),
            bLog.First(l => l.StartsWith("started", StringComparison.Ordinal)));
        Assert.Equal(userDatabaseBefore, UserDatabaseFile.State());
    }

    /// <summary>Runs the dispatcher until <paramref name="done"/> holds; bounded so a run that never settles fails.</summary>
    private static void PumpUntil(Func<bool> done)
    {
        long started = Stopwatch.GetTimestamp();
        while (!done())
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(20))
            {
                throw new TimeoutException("The sessions did not reach the expected state within 20 s.");
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
