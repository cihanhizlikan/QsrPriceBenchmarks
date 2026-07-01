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
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"qsr_seed_{Guid.NewGuid():N}.sqlite");
        try
        {
            long firstCount;
            using (var conn = Database.Open(path))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM QSR;";
                firstCount = Convert.ToInt64(cmd.ExecuteScalar());
                Assert.Equal(Chains.All.Count, (int)firstCount);
            }

            // Re-opening the same file must not duplicate seed rows.
            using (var conn = Database.Open(path))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM QSR;";
                Assert.Equal(firstCount, Convert.ToInt64(cmd.ExecuteScalar()));

                using var plat = conn.CreateCommand();
                plat.CommandText = "SELECT COUNT(*) FROM PLATFORMS WHERE NAME = 'Tıkla Gelsin';";
                Assert.Equal(1L, Convert.ToInt64(plat.ExecuteScalar()));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    [Fact(DisplayName = "Migration adds the QSR.LOGO_BLOB column")]
    public void Migration_AddsLogoBlobColumn()
    {
        using var db = new TempDb();
        using var cmd = db.Conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(QSR);";
        var columns = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        Assert.Contains("LOGO_BLOB", columns);
    }

    [Theory(DisplayName = "Reporting views exist and are queryable when empty")]
    [InlineData("V_LATEST_PRICES")]
    [InlineData("V_PRICE_BENCHMARKS")]
    [InlineData("V_MARKETING_REPORT")]
    public void Views_AreQueryable(string viewName)
    {
        using var db = new TempDb();
        // No snapshots yet → 0 rows, but the view must parse and execute.
        Assert.Equal(0L, Convert.ToInt64(db.Scalar($"SELECT COUNT(*) FROM {viewName};")));
    }

    [Fact(DisplayName = "Seed syncs SCRAPE_TABS to the chain config exactly (order preserved)")]
    public void Seed_WiresScrapeTabs()
    {
        using var db = new TempDb();
        var qsrId = Repository.QsrId(db.Conn, "Burger King");
        var platformId = Repository.PlatformId(db.Conn, "Tıkla Gelsin");

        var tabs = Repository.LoadScrapeTabs(db.Conn, qsrId, platformId);
        var expected = Chains.All
            .First(c => c.Name == "Burger King")
            .ScrapeTabs["Tıkla Gelsin"];

        Assert.Equal("Popüler Ürünler", tabs[0]);
        // Authoritative sync → DB list matches the config list verbatim and in order.
        Assert.Equal(expected, tabs);
    }

    [Fact(DisplayName = "Re-opening re-syncs tabs to config (drops tabs no longer listed)")]
    public void Seed_TabSync_RemovesStaleTabs()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"qsr_tabs_{Guid.NewGuid():N}.sqlite");
        try
        {
            long qsrId, platformId;
            using (var conn = Database.Open(path))
            {
                qsrId = Repository.QsrId(conn, "Burger King");
                platformId = Repository.PlatformId(conn, "Tıkla Gelsin");

                // Simulate a stale tab left over from an older config.
                using var ins = conn.CreateCommand();
                ins.CommandText = """
                    INSERT INTO SCRAPE_TABS (QSR_ID, PLATFORM_ID, TAB_NAME, DISPLAY_ORDER)
                    VALUES ($q, $p, 'Ekonomikings', 99);
                    """;
                ins.Parameters.AddWithValue("$q", qsrId);
                ins.Parameters.AddWithValue("$p", platformId);
                ins.ExecuteNonQuery();
            }

            // Re-opening runs the authoritative seed again and should prune it.
            using (var conn = Database.Open(path))
            {
                var tabs = Repository.LoadScrapeTabs(conn, qsrId, platformId);
                Assert.DoesNotContain("Ekonomikings", tabs);
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
