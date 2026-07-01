using ClosedXML.Excel;
using QsrPriceBenchmarks.Core.Export;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

[Trait("Category", "Unit")]
[Trait("Category", "Db")]
public sealed class ExcelExporterTests
{
    [Fact(DisplayName = "Export writes a workbook with header row and at least one data row")]
    public void Export_ProducesReadableWorkbook()
    {
        using var db = new TempDb();
        ReportSeed.Seed(db.Conn, first: 100m, second: 120m);

        var outDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"qsr_xlsx_{Guid.NewGuid():N}");
        try
        {
            var path = ExcelExporter.Export(ReportSeed.Chain, db.Conn, outDir);
            Assert.True(File.Exists(path), $"Export did not create a file at {path}");

            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet(1);

            Assert.Equal("Province", ws.Cell(1, 1).GetString());        // first header
            Assert.Equal("Menu Item", ws.Cell(1, 8).GetString());    // 8th header
            Assert.Equal("Whopper", ws.Cell(2, 8).GetString());      // first data row
            Assert.True(ws.LastRowUsed()!.RowNumber() >= 2, "Expected at least one data row.");
        }
        finally
        {
            try { if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true); } catch { }
        }
    }
}
