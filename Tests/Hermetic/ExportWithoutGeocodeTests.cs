using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Export;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// Regression for the Step-4 crash "data is NULL at ordinal 0" when exporting
/// before Step 3 geocoding has matched any platform location to a physical
/// LOCATIONS row. The QSR name must come from the scrape run (always present),
/// not the (NULL) matched location, and the exporter must tolerate NULL
/// location columns.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Db")]
public sealed class ExportWithoutGeocodeTests
{
    private const string Chain = "Burger King";

    /// <summary>Seed one scraped, UN-matched platform location (no geocoding).</summary>
    private static void SeedUnmatched(Microsoft.Data.Sqlite.SqliteConnection conn)
    {
        long qsr = Repository.QsrId(conn, Chain);
        long plat = Repository.PlatformId(conn, "Tıkla Gelsin");

        long plId = TestData.InsertPlatformLocation(conn, qsr, plat, "pl-x", "BK Avcilar");
        Repository.UpdatePlatformLocation(conn, plId, address: "Merkez Mah No 5");
        // Deliberately NO SetPlatformLocationMatch → LOCATION_ID stays NULL.

        long item = Repository.GetOrCreateMenuItem(conn, qsr, "Whopper");
        using RunLock run = Repository.StartScrapeRun(conn, qsr);
        Repository.InsertSnapshot(conn, run.RunId, plId, item, 100m);
        Repository.FinishScrapeRun(conn, run.RunId);
    }

    [Fact(DisplayName = "V_MARKETING_REPORT has a non-NULL QSR even when unmatched")]
    public void View_HasQsr_WhenUnmatched()
    {
        using TempDb db = new();
        SeedUnmatched(db.Conn);
        long qsr = Repository.QsrId(db.Conn, Chain);

        using SqliteCommand cmd = db.Conn.CreateCommand();
        cmd.CommandText = """
            SELECT QSR, LOCATION_NAME, PLATFORM_LOCATION_NAME
            FROM V_MARKETING_REPORT WHERE QSR_ID = $q;
            """;
        cmd.Parameters.AddWithValue("$q", qsr);

        using SqliteDataReader r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.False(r.IsDBNull(0));                 // QSR present
        Assert.Equal(Chain, r.GetString(0));
        Assert.True(r.IsDBNull(1));                  // LOCATION_NAME null (unmatched)
        Assert.False(r.IsDBNull(2));                 // platform location name present
    }

    [Fact(DisplayName = "Export succeeds before geocoding; platform data filled, location cells blank")]
    public void Export_Succeeds_WhenUnmatched()
    {
        using TempDb db = new();
        SeedUnmatched(db.Conn);

        string outDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"qsr_xlsx_{Guid.NewGuid():N}");
        try
        {
            // Previously threw: "The data is NULL at ordinal 0".
            string path = ExcelExporter.Export(Chain, db.Conn, outDir);
            Assert.True(File.Exists(path));

            using XLWorkbook wb = new(path);
            IXLWorksheet ws = wb.Worksheet(1);

            Assert.Equal("", ws.Cell(2, 1).GetString());            // Province blank (unmatched)
            Assert.Equal("", ws.Cell(2, 3).GetString());            // Location Name blank
            Assert.Equal("Bk Avcilar", ws.Cell(2, 5).GetString());  // platform location name present (Title-cased)
            Assert.Equal("Whopper", ws.Cell(2, 8).GetString());     // menu item present
        }
        finally
        {
            try { if (Directory.Exists(outDir)) { Directory.Delete(outDir, recursive: true); } } catch { }
        }
    }
}
