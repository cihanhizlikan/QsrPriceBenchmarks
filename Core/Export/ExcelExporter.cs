using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Core.Services;

namespace QsrPriceBenchmarks.Core.Export;

/// <summary>
/// Step 4: export V_MARKETING_REPORT rows for one chain to a styled Excel
/// workbook. When <paramref name="sinceRunId"/> is given, only snapshots
/// from that scrape run onwards are included.
/// </summary>
public static class ExcelExporter
{
    private static readonly string[] Headers =
    {
        "Province", "District", "Location Name", "Location Address",
        "Platform Location Name", "Platform Location Address",
        "Platform URL", "Menu Item", "Price", "Previous Price", "Price Change",
        "Last Scrape Run", "Days Since Scrape",
    };

    private static readonly string[] UnscrapedHeaders =
    {
        "Platform Location Name", "Address", "Tıkla Gelsin URL",
    };

    public static string Export(string chainName, SqliteConnection conn, string outputDir, long? sinceRunId = null,
                                IProgress<StepProgress>? progress = null, CancellationToken ct = default)
    {
        List<ReportRow> rows = LoadReportRows(chainName, conn, sinceRunId);
        int total = rows.Count;
        progress?.Report(new StepProgress("Exporting", 0, total));

        using XLWorkbook workbook = new();
        IXLWorksheet sheet = workbook.Worksheets.Add(SanitiseSheetName(chainName));

        for (int c = 0; c < Headers.Length; c++)
        {
            IXLCell cell = sheet.Cell(1, c + 1);
            cell.SetValue(Headers[c]);
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(BrandPalette.HexFor(chainName));
            cell.Style.Font.FontColor = XLColor.FromHtml("#FFFFFF");
        }

        int rowIdx = 2;
        int written = 0;
        foreach (ReportRow row in rows)
        {
            // Honour cancellation between rows; throwing here means no file is
            // written (SaveAs hasn't run yet), so a cancelled export leaves nothing.
            ct.ThrowIfCancellationRequested();
            sheet.Cell(rowIdx, 1).SetValue(row.Province ?? "");
            sheet.Cell(rowIdx, 2).SetValue(row.District ?? "");
            sheet.Cell(rowIdx, 3).SetValue(row.LocationName ?? "");
            sheet.Cell(rowIdx, 4).SetValue(row.LocationAddress ?? "");
            sheet.Cell(rowIdx, 5).SetValue(row.PlatformLocationName ?? "");
            sheet.Cell(rowIdx, 6).SetValue(row.PlatformLocationAddress ?? "");

            IXLCell urlCell = sheet.Cell(rowIdx, 7);
            if (!string.IsNullOrWhiteSpace(row.PlatformUrl))
            {
                urlCell.SetValue(row.PlatformUrl);
                urlCell.SetHyperlink(new XLHyperlink(row.PlatformUrl)); // real clickable link
            }
            else
            {
                urlCell.SetValue("");
            }

            sheet.Cell(rowIdx, 8).SetValue(row.MenuItem);
            sheet.Cell(rowIdx, 9).SetValue(row.Price);
            sheet.Cell(rowIdx, 10).SetValue(row.PrevPrice?.ToString() ?? "");

            IXLCell changeCell = sheet.Cell(rowIdx, 11);
            changeCell.SetValue(row.PriceChange);
            if (row.PriceChange > 0)
            {
                changeCell.Style.Font.FontColor = XLColor.FromHtml("#C0392B"); // red — price up
            }
            else if (row.PriceChange < 0)
            {
                changeCell.Style.Font.FontColor = XLColor.FromHtml("#27AE60"); // green — price down
            }

            sheet.Cell(rowIdx, 12).SetValue(row.LastScrapeRun);
            sheet.Cell(rowIdx, 13).SetValue(row.DaysSinceScrape);

            rowIdx++;
            written++;
            // Report periodically (and on the final row) to keep the UI smooth
            // without flooding the dispatcher on large exports.
            if ((written % 250) == 0 || written == total)
            {
                progress?.Report(new StepProgress("Exporting", written, total));
            }
        }

        sheet.Columns().AdjustToContents();

        // Second worksheet: platform locations with no snapshot in the latest run
        // (out of order / otherwise unscraped) — previously a dedicated UI tab.
        AddUnscrapedSheet(workbook, chainName, conn);

        // Serialising the workbook is a single blocking step that can't be
        // cancelled mid-write; surface it as a distinct phase so the bar doesn't
        // appear stuck at 100% on large files.
        progress?.Report(new StepProgress("Writing file", total, total));

        Directory.CreateDirectory(outputDir);
        string safeChain = SanitiseSheetName(chainName).Replace(' ', '_');
        string fileName = $"{safeChain}_price_benchmarks_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
        string fullPath = Path.Combine(outputDir, fileName);

        workbook.SaveAs(fullPath);
        return fullPath;
    }

    /// <summary>
    /// Add an "Unscraped" worksheet listing this chain's platform locations that
    /// produced no price snapshot in the latest finished run — temporarily out of
    /// order or otherwise unscraped — with a clickable TG link for each.
    /// </summary>
    private static void AddUnscrapedSheet(XLWorkbook workbook, string chainName, SqliteConnection conn)
    {
        long qsrId = Repository.QsrId(conn, chainName);
        List<UnscrapedLocationRow> rows = Repository.GetUnscrapedPlatformLocations(conn, qsrId);

        // Don't clutter the workbook with an empty sheet when every location was
        // scraped — only add the worksheet if there's something to report.
        if (rows.Count == 0)
        {
            return;
        }

        IXLWorksheet sheet = workbook.Worksheets.Add("Unscraped");

        for (int c = 0; c < UnscrapedHeaders.Length; c++)
        {
            IXLCell cell = sheet.Cell(1, c + 1);
            cell.SetValue(UnscrapedHeaders[c]);
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(BrandPalette.HexFor(chainName));
            cell.Style.Font.FontColor = XLColor.FromHtml("#FFFFFF");
        }

        int rowIdx = 2;
        foreach (UnscrapedLocationRow row in rows)
        {
            sheet.Cell(rowIdx, 1).SetValue(row.Name ?? "");
            sheet.Cell(rowIdx, 2).SetValue(row.Address ?? "");

            IXLCell urlCell = sheet.Cell(rowIdx, 3);
            if (!string.IsNullOrWhiteSpace(row.Url))
            {
                urlCell.SetValue(row.Url);
                urlCell.SetHyperlink(new XLHyperlink(row.Url));
            }
            else
            {
                urlCell.SetValue("");
            }
            rowIdx++;
        }

        sheet.Columns().AdjustToContents();
    }

    private static string SanitiseSheetName(string name)
    {
        // Excel sheet names: max 31 chars, no \ / ? * [ ] :
        var invalid = new[] { '\\', '/', '?', '*', '[', ']', ':' };
        var cleaned = new string(name.Where(c => !invalid.Contains(c)).ToArray());
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }

    private sealed record ReportRow(
        string Qsr, string? Province, string? District, string? LocationName, string? LocationAddress,
        string Platform, string? PlatformLocationName, string? PlatformLocationAddress, string? PlatformUrl,
        string MenuItem, decimal Price, decimal? PrevPrice, decimal PriceChange,
        string LastScrapeRun, int DaysSinceScrape);

    private static List<ReportRow> LoadReportRows(string chainName, SqliteConnection conn, long? sinceRunId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT QSR, PROVINCE, DISTRICT, LOCATION_NAME, LOCATION_ADDRESS,
                   PLATFORM, PLATFORM_LOCATION_NAME, PLATFORM_LOCATION_ADDRESS, PLATFORM_URL,
                   MENU_ITEM, PRICE, PREV_PRICE, PRICE_CHANGE,
                   LAST_SCRAPE_RUN, DAYS_SINCE_SCRAPE
            FROM V_MARKETING_REPORT
            WHERE QSR_ID = (SELECT ID FROM QSR WHERE NAME = $chain)
            """ + (sinceRunId is not null ? " AND SCRAPE_RUN_ID >= $since" : "") + """
            ORDER BY PROVINCE, DISTRICT, LOCATION_NAME, MENU_ITEM;
            """;
        cmd.Parameters.AddWithValue("$chain", chainName);
        if (sinceRunId is not null)
            cmd.Parameters.AddWithValue("$since", sinceRunId.Value);

        var result = new List<ReportRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ReportRow(
                Qsr: reader.IsDBNull(0) ? "" : reader.GetString(0),
                Province: reader.IsDBNull(1) ? null : reader.GetString(1),
                District: reader.IsDBNull(2) ? null : reader.GetString(2),
                LocationName: reader.IsDBNull(3) ? null : reader.GetString(3),
                LocationAddress: reader.IsDBNull(4) ? null : reader.GetString(4),
                Platform: reader.IsDBNull(5) ? "" : reader.GetString(5),
                PlatformLocationName: reader.IsDBNull(6) ? null : reader.GetString(6),
                PlatformLocationAddress: reader.IsDBNull(7) ? null : reader.GetString(7),
                PlatformUrl: reader.IsDBNull(8) ? null : reader.GetString(8),
                MenuItem: reader.IsDBNull(9) ? "" : reader.GetString(9),
                Price: reader.IsDBNull(10) ? 0m : Convert.ToDecimal(reader.GetDouble(10)),
                PrevPrice: reader.IsDBNull(11) ? null : Convert.ToDecimal(reader.GetDouble(11)),
                PriceChange: reader.IsDBNull(12) ? 0m : Convert.ToDecimal(reader.GetDouble(12)),
                LastScrapeRun: reader.IsDBNull(13) ? "" : reader.GetString(13),
                DaysSinceScrape: reader.IsDBNull(14) ? 0 : reader.GetInt32(14)));
        }
        return result;
    }
}
