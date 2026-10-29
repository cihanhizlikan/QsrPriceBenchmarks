using System.Diagnostics;
using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Core.Scraping;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// The claim a scrape holds on its run (plan WP-C1). The requirement, maintainer 2026-10-29: a run must not be
/// worked on by two instances, and a crash must never leave it marked as being worked on.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Db")]
public sealed class RunLockTests
{
    private static long QsrId(TempDb db)
    {
        return Repository.QsrId(db.Conn, ChainRegistry.All[0].Name);
    }

    [Fact(DisplayName = "A new run is claimed: a second claim fails, another connection sees it held, release frees it")]
    public void NewRun_IsClaimedUntilReleased()
    {
        using TempDb db = new();
        long runId;
        using (RunLock run = Repository.StartScrapeRun(db.Conn, QsrId(db)))
        {
            runId = run.RunId;
            Assert.Null(RunLock.TryAcquire(db.Conn, runId));

            // Another window is another connection.
            using SqliteConnection other = Database.Open(db.Path);
            Assert.True(RunLock.IsHeld(other, runId));
        }

        Assert.False(RunLock.IsHeld(db.Conn, runId));
        Assert.False(File.Exists($"{db.Path}.run-{runId}.lock"), "a released claim leaves no file behind");
    }

    [Fact(DisplayName = "A process killed while holding the lock file leaves the run free to claim")]
    public void KilledHolder_LeavesTheRunFree()
    {
        using TempDb db = new();
        long runId;
        using (RunLock run = Repository.StartScrapeRun(db.Conn, QsrId(db)))
        {
            runId = run.RunId;
        }
        string path = $"{db.Path}.run-{runId}.lock";

        // A second process with the lock file open, waiting on its input: cmd's redirection holds the handle.
        using Process holder = Process.Start(new ProcessStartInfo("cmd.exe", $"/c set /p x= > \"{path}\"")
        {
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        try
        {
            WaitUntil(() => RunLock.IsHeld(db.Conn, runId), "the holder process never opened the lock file");
            Assert.Null(RunLock.TryAcquire(db.Conn, runId));
        }
        finally
        {
            holder.Kill(entireProcessTree: true);
            Assert.True(holder.WaitForExit(10_000), "the holder process did not die");
        }

        Assert.True(File.Exists(path), "the killed holder's file is still on disk — the case being tested");
        Assert.False(RunLock.IsHeld(db.Conn, runId));
        using RunLock reclaimed = RunLock.TryAcquire(db.Conn, runId)
            ?? throw new Xunit.Sdk.XunitException("a crashed holder's run could not be claimed");
    }

    [Fact(DisplayName = "Resuming a run that is being scraped is refused before anything else happens")]
    public async Task Resume_OfAClaimedRun_IsRefused()
    {
        using TempDb db = new();
        ChainConfig chain = ChainRegistry.All[0];
        using RunLock live = Repository.StartScrapeRun(db.Conn, QsrId(db));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TgScraper.ScrapeAsync(chain, db.Conn, resumeRunId: live.RunId));

        Assert.Contains($"run {live.RunId}", refused.Message, StringComparison.Ordinal);
    }

    private static void WaitUntil(Func<bool> done, string failure)
    {
        Stopwatch clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10))
            {
                throw new Xunit.Sdk.XunitException(failure);
            }
            Thread.Sleep(50);
        }
    }
}
