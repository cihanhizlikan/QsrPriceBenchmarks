using QsrPriceBenchmarks.Ui;
using QsrPriceBenchmarks.Ui.ViewModels;
using Xunit;
using static QsrPriceBenchmarks.UiTests.Dispatching;

namespace QsrPriceBenchmarks.UiTests;

/// <summary>
/// The maintainer's request, 2026-10-29 (plan WP-C2): switch chains mid-scrape to start, watch or cancel other
/// scrapes, each chain with its own log and progress. The pipeline is stood in by a run that logs, reports progress
/// and waits for its cancellation — the window, pipelines and commands are the real ones.
/// </summary>
public sealed class ChainPipelinesTests
{
    private const string Stuck = "The pipelines did not reach the expected state";

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
                PumpUntil(() => startup.IsCompleted, Stuck);

                ChainViewModel a = model.Chains[0], b = model.Chains[1];
                model.SelectedChain = a;
                model.RunCommand.Execute(null);
                PumpUntil(() => a.Pipeline.ProgressTotal == 10, Stuck);

                // The chain list is not locked: another chain can be chosen and started while A runs.
                model.SelectedChain = b;
                Assert.True(model.RunCommand.CanExecute(null), "Run must be available for an idle chain while another runs");
                model.RunCommand.Execute(null);
                PumpUntil(() => b.Pipeline.ProgressTotal == 10, Stuck);
                bothRunning = a.Pipeline.IsRunning && b.Pipeline.IsRunning && model.IsAnyRunning;
                aStatusWhileBothRun = a.Pipeline.StatusText;

                // Cancel acts on the chain on screen only.
                model.SelectedChain = a;
                model.CancelCommand.Execute(null);
                PumpUntil(() => !a.Pipeline.IsRunning, Stuck);
                bRunningAfterACancelled = b.Pipeline.IsRunning;
                aStatusAfter = a.Pipeline.StatusText;

                model.SelectedChain = b;
                model.CancelCommand.Execute(null);
                PumpUntil(() => !b.Pipeline.IsRunning, Stuck);
                bStatusAfter = b.Pipeline.StatusText;
                anyRunningAtEnd = model.IsAnyRunning;
                aLog.AddRange(a.Pipeline.LogLines);
                bLog.AddRange(b.Pipeline.LogLines);
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
}
