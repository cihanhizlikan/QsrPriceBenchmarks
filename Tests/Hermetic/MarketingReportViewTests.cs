using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// Seeds one fully-matched chain location with two scrape runs so the
/// price-history logic in the views has something to compute against.
/// Shared by the view and Excel-export tests.
/// </summary>
internal static class ReportSeed
{
    public const string Chain = "Burger King";

    /// <summary>Returns the menu item name seeded.</summary>
    public static string Seed(SqliteConnection conn, decimal first = 100m, decimal second = 120m)
    {
        var qsr = Repository.QsrId(conn, Chain);
        var plat = Repository.PlatformId(conn, "Tıkla Gelsin");

        var locId = Repository.UpsertLocation(conn, "Istanbul", "Kadikoy", "loc-1", qsr)!.Value;
        Repository.UpdateLocationDetails(conn, "loc-1", "BK Kadikoy", "Caferaga Mah No 1", qsr);

        var plId = Repository.UpsertPlatformLocation(conn, qsr, plat, "pl-1", "BK Kadikoy")!.Value;
        Repository.UpdatePlatformLocation(conn, plId, address: "Caferaga Mah No 1");
        Repository.SetPlatformLocationMatch(conn, plId, locId);

        var item = Repository.GetOrCreateMenuItem(conn, qsr, "Whopper");

        var run1 = Repository.StartScrapeRun(conn, qsr);
        Repository.InsertSnapshot(conn, run1, plId, item, first);
        Repository.FinishScrapeRun(conn, run1);

        var run2 = Repository.StartScrapeRun(conn, qsr);
        Repository.InsertSnapshot(conn, run2, plId, item, second);
        Repository.FinishScrapeRun(conn, run2);

        return "Whopper";
    }
}

[Trait("Category", "Unit")]
[Trait("Category", "Db")]
public sealed class MarketingReportViewTests
{
    [Fact(DisplayName = "V_MARKETING_REPORT reports current price, previous price, and the delta")]
    public void Report_ComputesPriceChange()
    {
        using var db = new TempDb();
        ReportSeed.Seed(db.Conn, first: 100m, second: 120m);
        var qsr = Repository.QsrId(db.Conn, ReportSeed.Chain);

        using var cmd = db.Conn.CreateCommand();
        cmd.CommandText = """
            SELECT PRICE, PREV_PRICE, PRICE_CHANGE
            FROM V_MARKETING_REPORT
            WHERE QSR_ID = $q AND MENU_ITEM = 'Whopper';
            """;
        cmd.Parameters.AddWithValue("$q", qsr);

        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(120.0, r.GetDouble(0), 3);   // current = latest run
        Assert.Equal(100.0, r.GetDouble(1), 3);   // previous = earlier run
        Assert.Equal(20.0, r.GetDouble(2), 3);    // change = +20
        Assert.False(r.Read());                    // exactly one row
    }
}
