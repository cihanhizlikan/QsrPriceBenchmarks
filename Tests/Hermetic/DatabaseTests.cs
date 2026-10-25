using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

[Trait("Category", "Unit")]
[Trait("Category", "Db")]
public sealed class DatabaseTests
{
    [Fact(DisplayName = "Open creates schema, seeds every chain, and is idempotent")]
    public void Open_SeedsChains_Idempotently()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"qsr_seed_{Guid.NewGuid():N}.sqlite");
        try
        {
            long firstCount;
            using (SqliteConnection conn = Database.Open(path))
            {
                using SqliteCommand cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM QSR;";
                firstCount = Convert.ToInt64(cmd.ExecuteScalar());
                Assert.Equal(ChainRegistry.All.Count, (int)firstCount);
            }

            // Re-opening the same file must not duplicate seed rows.
            using (SqliteConnection conn = Database.Open(path))
            {
                using SqliteCommand cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM QSR;";
                Assert.Equal(firstCount, Convert.ToInt64(cmd.ExecuteScalar()));

                using SqliteCommand plat = conn.CreateCommand();
                plat.CommandText = "SELECT COUNT(*) FROM PLATFORMS WHERE NAME = 'Tıkla Gelsin';";
                Assert.Equal(1L, Convert.ToInt64(plat.ExecuteScalar()));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(path)) { File.Delete(path); } } catch { }
        }
    }

    [Fact(DisplayName = "Migration adds the QSR.LOGO_BLOB column")]
    public void Migration_AddsLogoBlobColumn()
    {
        using TempDb db = new();
        using SqliteCommand cmd = db.Conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(QSR);";
        List<string> columns = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("LOGO_BLOB", columns);
    }

    [Theory(DisplayName = "Reporting views exist and are queryable when empty")]
    [InlineData("V_LATEST_PRICES")]
    [InlineData("V_PRICE_BENCHMARKS")]
    [InlineData("V_MARKETING_REPORT")]
    public void Views_AreQueryable(string viewName)
    {
        using TempDb db = new();
        // No snapshots yet → 0 rows, but the view must parse and execute.
        Assert.Equal(0L, Convert.ToInt64(db.Scalar($"SELECT COUNT(*) FROM {viewName};")));
    }

    [Fact(DisplayName = "Seed creates no tabs — the tab list belongs to run-time discovery")]
    public void Seed_CreatesNoTabs()
    {
        using TempDb db = new();
        long qsrId = Repository.QsrId(db.Conn, "Burger King");
        long platformId = Repository.PlatformId(db.Conn, "Tıkla Gelsin");

        // Nothing hardcoded: a fresh database starts with an empty tab list.
        // Step 2's first run discovers the tabs live from the platform's pages;
        // re-opens preserve that runtime state (pinned by the test below).
        Assert.Empty(Repository.LoadScrapeTabs(db.Conn, qsrId, platformId));
    }

    [Fact(DisplayName = "Re-opening preserves unknown tabs; a clean run soft-deletes them")]
    public void Seed_PreservesUnknownTabs_RunReconciliationSoftDeletes()
    {
        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"qsr_tabs_{Guid.NewGuid():N}.sqlite");
        try
        {
            long qsrId, platformId;
            using (SqliteConnection conn = Database.Open(path))
            {
                qsrId = Repository.QsrId(conn, "Burger King");
                platformId = Repository.PlatformId(conn, "Tıkla Gelsin");

                // All tabs enter through run-time discovery — nothing is seeded.
                Repository.UpsertDiscoveredScrapeTab(conn, qsrId, platformId, "Popüler Ürünler");
                Repository.UpsertDiscoveredScrapeTab(conn, qsrId, platformId, "Ekonomikings");
            }
            SqliteConnection.ClearAllPools();

            using (SqliteConnection conn = Database.Open(path))
            {
                // Re-opening re-runs the seed, which must NOT touch discovered
                // tabs (the old wipe-and-resync-to-config would have).
                List<string> tabs = Repository.LoadScrapeTabs(conn, qsrId, platformId);
                Assert.Contains("Ekonomikings", tabs);

                // A cleanly completed run that saw every tab EXCEPT the
                // discovered one is what removes it — as a soft delete.
                List<string> observed = [.. tabs.Where(t => t != "Ekonomikings")];
                List<string> deactivated =
                    Repository.ReconcileScrapeTabs(conn, qsrId, platformId, observed);

                Assert.Equal(new[] { "Ekonomikings" }, deactivated.ToArray());
                Assert.DoesNotContain("Ekonomikings",
                    Repository.LoadScrapeTabs(conn, qsrId, platformId));

                // Soft, not hard: the row survives with ACTIVE = 0 so a later
                // rediscovery reactivates it with its history intact.
                using SqliteCommand check = conn.CreateCommand();
                check.CommandText = """
                    SELECT ACTIVE FROM SCRAPE_TABS
                    WHERE QSR_ID = $q AND PLATFORM_ID = $p AND TAB_NAME = 'Ekonomikings';
                    """;
                check.Parameters.AddWithValue("$q", qsrId);
                check.Parameters.AddWithValue("$p", platformId);
                Assert.Equal(0L, Convert.ToInt64(check.ExecuteScalar()));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (string suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); } catch { /* best-effort */ }
            }
        }
    }
}
