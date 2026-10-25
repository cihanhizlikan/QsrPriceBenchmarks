using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// SCRAPE_TABS lifecycle: Step 2 discovers tabs seen live on the platform
/// (selectable from the NEXT run) and soft-deletes tabs a cleanly completed
/// run no longer saw anywhere (ACTIVE = 0, LAST_SEEN_AT keeps recording the
/// last run that did see the tab). Nothing is hardcoded: discovery is the ONLY
/// way tabs enter the database, and re-opening it must neither resurrect a
/// soft-deleted tab nor erase a discovered one.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Db")]
public sealed class ScrapeTabLifecycleTests
{
    private static (long QsrId, long PlatformId) Ids(SqliteConnection conn)
    {
        ChainConfig chain = ChainRegistry.All.First(c => c.Name == "Burger King");
        return (Repository.QsrId(conn, chain.Name),
                Repository.PlatformId(conn, chain.PrimaryPlatform));
    }

    [Fact(DisplayName = "Discovered tab is appended after every existing tab, active, stamped")]
    public void Discovery_AppendsActiveStampedTab()
    {
        using TempDb db = new();
        (long qsrId, long platformId) = Ids(db.Conn);
        // Fresh database → empty list (tabs are never hardcoded).
        List<string> seeded = Repository.LoadScrapeTabs(db.Conn, qsrId, platformId);
        Assert.Empty(seeded);

        Repository.UpsertDiscoveredScrapeTab(db.Conn, qsrId, platformId, "Yeni Sekme");

        List<string> after = Repository.LoadScrapeTabs(db.Conn, qsrId, platformId);
        Assert.Equal(seeded.Append("Yeni Sekme").ToArray(), after.ToArray());

        object? seen = db.Scalar(
            "SELECT LAST_SEEN_AT FROM SCRAPE_TABS WHERE QSR_ID = $q AND PLATFORM_ID = $p AND TAB_NAME = 'Yeni Sekme';",
            ("$q", qsrId), ("$p", platformId));
        Assert.False(seen is null or DBNull);
    }

    [Fact(DisplayName = "Reconcile soft-deletes unobserved tabs and stamps observed ones")]
    public void Reconcile_SoftDeletesUnobserved()
    {
        using TempDb db = new();
        (long qsrId, long platformId) = Ids(db.Conn);
        // Fresh databases have no tabs (nothing is hardcoded); seed this test's
        // fixture the same way production does — through discovery.
        foreach (string tab in new[] { "Popüler Ürünler", "Kampanyalar", "Burgerler" })
        {
            Repository.UpsertDiscoveredScrapeTab(db.Conn, qsrId, platformId, tab);
        }
        List<string> tabs = Repository.LoadScrapeTabs(db.Conn, qsrId, platformId);

        string vanished = tabs[^1];
        List<string> observed = [.. tabs.Take(tabs.Count - 1)];

        List<string> deactivated =
            Repository.ReconcileScrapeTabs(db.Conn, qsrId, platformId, observed);

        Assert.Equal(new[] { vanished }, deactivated.ToArray());
        Assert.Equal(observed.ToArray(),
            Repository.LoadScrapeTabs(db.Conn, qsrId, platformId).ToArray());

        // Soft delete, not hard delete: the row survives with ACTIVE = 0.
        object? active = db.Scalar(
            "SELECT ACTIVE FROM SCRAPE_TABS WHERE QSR_ID = $q AND PLATFORM_ID = $p AND TAB_NAME = $t;",
            ("$q", qsrId), ("$p", platformId), ("$t", vanished));
        Assert.Equal(0L, Convert.ToInt64(active));

        // Every observed tab was stamped as seen just now.
        object? unstamped = db.Scalar(
            "SELECT COUNT(*) FROM SCRAPE_TABS WHERE QSR_ID = $q AND PLATFORM_ID = $p AND ACTIVE = 1 AND LAST_SEEN_AT IS NULL;",
            ("$q", qsrId), ("$p", platformId));
        Assert.Equal(0L, Convert.ToInt64(unstamped));
    }

    [Fact(DisplayName = "Re-open preserves discovered + soft-deleted state; rediscovery reactivates the same row")]
    public void Reopen_PreservesRuntimeState_AndRediscoveryReactivates()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qsr-tabs-{Guid.NewGuid():N}.sqlite");
        try
        {
            long qsrId, platformId;
            string vanished;
            int rowsBefore;

            using (SqliteConnection conn = Database.Open(path))
            {
                (qsrId, platformId) = Ids(conn);
                foreach (string tab in new[] { "Popüler Ürünler", "Kampanyalar" })
                {
                    Repository.UpsertDiscoveredScrapeTab(conn, qsrId, platformId, tab);
                }
                List<string> tabs = Repository.LoadScrapeTabs(conn, qsrId, platformId);
                vanished = tabs[^1];

                Repository.UpsertDiscoveredScrapeTab(conn, qsrId, platformId, "Tatlılar");
                Repository.ReconcileScrapeTabs(conn, qsrId, platformId,
                    [.. tabs.Take(tabs.Count - 1), "Tatlılar"]);

                rowsBefore = Convert.ToInt32(ScalarOn(conn,
                    "SELECT COUNT(*) FROM SCRAPE_TABS WHERE QSR_ID = $q AND PLATFORM_ID = $p;",
                    qsrId, platformId));
            }
            SqliteConnection.ClearAllPools();

            // Second open re-runs the ChainConfig seed. The discovered tab must
            // survive it and the soft-deleted tab must stay soft-deleted.
            using (SqliteConnection conn = Database.Open(path))
            {
                List<string> tabs = Repository.LoadScrapeTabs(conn, qsrId, platformId);
                Assert.Contains("Tatlılar", tabs);
                Assert.DoesNotContain(vanished, tabs);

                // The platform restores the vanished tab: discovery reactivates
                // the SAME row instead of inserting a duplicate.
                Repository.UpsertDiscoveredScrapeTab(conn, qsrId, platformId, vanished);
                Assert.Contains(vanished,
                    Repository.LoadScrapeTabs(conn, qsrId, platformId));

                object? rowsAfter = ScalarOn(conn,
                    "SELECT COUNT(*) FROM SCRAPE_TABS WHERE QSR_ID = $q AND PLATFORM_ID = $p;",
                    qsrId, platformId);
                Assert.Equal(rowsBefore, Convert.ToInt32(rowsAfter));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); } catch { /* best-effort */ }
            }
        }
    }

    private static object? ScalarOn(SqliteConnection conn, string sql, long qsrId, long platformId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("$q", qsrId);
        cmd.Parameters.AddWithValue("$p", platformId);
        return cmd.ExecuteScalar();
    }
}
