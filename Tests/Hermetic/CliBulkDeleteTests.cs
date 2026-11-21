using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// WP-R3, 2026-11-21: <c>--delete-scrape-runs-before-id</c> says what it would delete and asks, as the single delete
/// and the window do, because a deleted run's prices are history no rescan brings back. Until then it deleted at
/// once. The real <c>qsr.exe</c> is run, answering on standard input.
/// </summary>
[Trait("Category", "Unit")]
public sealed class CliBulkDeleteTests
{
    [Fact(DisplayName = "The bulk delete states what it would delete and deletes nothing without a yes")]
    public void BulkDelete_AsksFirst()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // qsr.exe drives the SQLite that ships with Windows, the only platform it ships for
        }

        using Seeded seeded = new();

        foreach (string answer in (string[])["n\n", ""])
        {
            (int exitCode, string output) = QsrProcess.Run(
                answer, "--db", seeded.Path, "--delete-scrape-runs-before-id", seeded.Last.ToString());

            Assert.Equal(0, exitCode);
            Assert.Contains($"About to delete 2 run(s) with ID < {seeded.Last} and 3 snapshot(s).", output,
                StringComparison.Ordinal);
            Assert.Contains("Aborted.", output, StringComparison.Ordinal);
            Assert.Equal(3, seeded.RunsLeft());
        }
    }

    [Fact(DisplayName = "The bulk delete deletes the earlier runs on a yes")]
    public void BulkDelete_DeletesOnYes()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using Seeded seeded = new();

        (int exitCode, string output) = QsrProcess.Run(
            "y\n", "--db", seeded.Path, "--delete-scrape-runs-before-id", seeded.Last.ToString());

        Assert.Equal(0, exitCode);
        Assert.Contains($"Deleted 2 run(s) with ID < {seeded.Last}.", output, StringComparison.Ordinal);
        Assert.Equal(1, seeded.RunsLeft());
    }

    /// <summary>Three finished runs, the first two holding three snapshots between them, in a closed file.</summary>
    private sealed class Seeded : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"qsr_cli_{Guid.NewGuid():N}.sqlite");

        public long Last { get; }

        public Seeded()
        {
            using SqliteConnection conn = Database.Open(Path);
            long qsr = Repository.QsrId(conn, "Burger King");
            long platform = Repository.PlatformId(conn, "Tıkla Gelsin");
            long location = TestData.InsertPlatformLocation(conn, qsr, platform, "bk-test");
            long whopper = Repository.GetOrCreateMenuItem(conn, qsr, "Whopper");
            long fries = Repository.GetOrCreateMenuItem(conn, qsr, "Patates");
            long first = Finished(conn, qsr);
            Repository.InsertSnapshot(conn, first, location, whopper, 100m);
            Repository.InsertSnapshot(conn, first, location, fries, 40m);
            long second = Finished(conn, qsr);
            Repository.InsertSnapshot(conn, second, location, whopper, 110m);
            Last = Finished(conn, qsr);
            SqliteConnection.ClearAllPools();
        }

        public int RunsLeft()
        {
            using SqliteConnection conn = Database.Open(Path);
            int left = Repository.ListScrapeRuns(conn).Count;
            SqliteConnection.ClearAllPools();
            return left;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (string file in (string[])[Path, Path + "-wal", Path + "-shm"])
            {
                try { File.Delete(file); } catch (IOException) { /* best effort */ }
            }
        }

        private static long Finished(SqliteConnection conn, long qsr)
        {
            using RunLock run = Repository.StartScrapeRun(conn, qsr);
            Repository.FinishScrapeRun(conn, run.RunId);
            return run.RunId;
        }
    }
}
