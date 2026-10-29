using System.Diagnostics;
using QsrPriceBenchmarks.Core.Scraping;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// The shared browser's lock is held only to launch or close the browser, and every wait on it is bounded (plan
/// WP-R1): a launch or close that hangs fails the run waiting behind it — or Cancel stops it — instead of stalling
/// every chain's next scrape forever. The lock is held here through the internal seam, so no browser starts.
/// </summary>
[Collection(nameof(BrowserSessionLockTests))]
[Trait("Category", "Unit")]
public sealed class BrowserSessionLockTests
{
    [Fact(DisplayName = "A run waiting on a stuck browser lock fails at the bound instead of waiting forever")]
    public async Task StuckLock_FailsAtTheBound()
    {
        TimeSpan saved = BrowserSession.SharedLockBound;
        await BrowserSession.SharedLock.WaitAsync();
        try
        {
            BrowserSession.SharedLockBound = TimeSpan.FromMilliseconds(500);
            Stopwatch clock = Stopwatch.StartNew();

            await Assert.ThrowsAsync<TimeoutException>(() =>
                BrowserSession.LaunchAsync().WaitAsync(TimeSpan.FromSeconds(30)));

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"gave up after {clock.Elapsed}");
        }
        finally
        {
            BrowserSession.SharedLockBound = saved;
            BrowserSession.SharedLock.Release();
        }
    }

    [Fact(DisplayName = "Cancel reaches a run that is waiting for the shared browser")]
    public async Task WaitingRun_IsCancellable()
    {
        await BrowserSession.SharedLock.WaitAsync();
        try
        {
            using CancellationTokenSource cancel = new(TimeSpan.FromMilliseconds(300));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                BrowserSession.LaunchAsync(ct: cancel.Token).WaitAsync(TimeSpan.FromSeconds(30)));
        }
        finally
        {
            BrowserSession.SharedLock.Release();
        }
    }
}

/// <summary>
/// Runs alone: these tests hold the process-wide browser lock, which would stall a live test's real launch.
/// </summary>
[CollectionDefinition(nameof(BrowserSessionLockTests), DisableParallelization = true)]
public sealed class BrowserSessionLockCollection;
