using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Cli;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Export;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Core.Services;
using QsrPriceBenchmarks.Core.Util;

// Colour output matches the Python prototype exactly via ConsoleColouriser.
// Disable colour when stdout is redirected (mirrors Python's os.isatty(1)).
ConsoleColouriser.Enabled = !Console.IsOutputRedirected;

// Ensure the Windows console renders UTF-8 box-drawing + ✓✗→ glyphs correctly.
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* ignore on redirected */ }

// All console output goes through one reporter so the live Step-3 progress bar
// never gets interleaved with log lines (synchronous, so output stays ordered).
ConsoleReporter reporter = new();
void Log(string msg) => reporter.Log(msg);
IProgress<string> progress = new SyncProgress<string>(reporter.Log);
IProgress<StepProgress> progressCount = new SyncProgress<StepProgress>(reporter.Bar);

void PrintBanner()
{
    Console.WriteLine();
    foreach (string line in ConsoleColouriser.BannerLines())
    {
        Console.WriteLine(line);
    }
    Console.WriteLine();
    Console.WriteLine(ConsoleColouriser.BannerTitle());
}

void PrintHelp() => Console.WriteLine("""

    QSR Price Benchmarks — restaurant menu price scraper

    Usage:
      qsr --chain CHAIN_SLUG [options]      Full run or selective steps
      qsr --list-scrape-runs
      qsr --delete-scrape-run RUN_ID
      qsr --delete-scrape-runs-before-id RUN_ID

    Options:
      --db DB                           Database path (default: QsrPriceBenchmarks.sqlite)
      --chain CHAIN_SLUG                 Chain slug (e.g. burger-king, popeyes, usta-donerci)
      --force-crawl                     Run Step 1: re-crawl chain website
      --geocode                         Run Step 3 (geocoding + matching of unmatched rows)
      --geocode-rematch                 Run Step 3: reset all matches first, then rematch
      --export                          Run Step 4 (export all snapshots to Excel)
      --export-since RUN_ID             Run Step 4 (export snapshots from RUN_ID onwards)
      --delete-scrape-run RUN_ID        Delete exactly this run and its snapshots, then exit
      --delete-scrape-runs-before-id N  Delete all runs with ID < N, then exit
      --list-scrape-runs                Print all scrape runs and exit

    Available chain slugs: burger-king, popeyes, usta-donerci, arbys, sbarro, usta-pideci, subway
    """);

// ── Parse ─────────────────────────────────────────────────────────────────────
CliOptions opts;
try { opts = CliParser.Parse(args); }
catch (CliArgumentException ex) { Console.Error.WriteLine($"error: {ex.Message}"); return 1; }

if (opts.Help) { PrintBanner(); PrintHelp(); return 0; }

PrintBanner();
Log(ConsoleColouriser.DbLine(Path.GetFullPath(opts.Db)));
Log(ConsoleColouriser.Separator(56));
Console.WriteLine();

// Database.Open refuses a file that is not ours (InvalidOperationException) and
// SQLite refuses one that is not a database at all; either way nothing can run,
// so say why and exit rather than printing a stack trace.
SqliteConnection? opened;
try
{
    opened = Database.Open(opts.Db);
}
catch (Exception ex) when (ex is InvalidOperationException or SqliteException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
using SqliteConnection conn = opened;

// ── Maintenance commands ──────────────────────────────────────────────────────
if (opts.ListScrapeRuns)
{
    List<ScrapeRunRow> runs = Repository.ListScrapeRuns(conn);
    if (runs.Count == 0) { Log("  No scrape runs recorded yet."); return 0; }

    foreach (ScrapeRunRow r in runs)
    {
        Log($"  [run: {r.Id}, qsr: {r.QsrId}, started: {r.StartedAt}, " +
            $"finished: {r.FinishedAt ?? "(running)"}]");
    }
    return 0;
}

if (opts.DeleteScrapeRun is not null)
{
    (bool found, long qsrId, string startedAt, int snapCount) = Repository.GetScrapeRunForDeletion(conn, opts.DeleteScrapeRun.Value);
    if (!found) { Log($"  ✗ Scrape run {opts.DeleteScrapeRun} not found."); return 1; }
    Log($"  About to delete run {opts.DeleteScrapeRun} (QSR_ID {qsrId}, {startedAt}) " +
        $"and {snapCount} snapshot(s).");
    Console.Write("  Confirm? [y/N] ");
    if (Console.ReadLine()?.Trim().ToLowerInvariant() != "y") { Log("  Aborted."); return 0; }
    Repository.DeleteScrapeRun(conn, opts.DeleteScrapeRun.Value);
    Log($"  ✓ Deleted run {opts.DeleteScrapeRun} and {snapCount} snapshot(s).");
    return 0;
}

if (opts.DeleteScrapeRunsBeforeId is not null)
{
    int affected = Repository.DeleteScrapeRunsBefore(conn, opts.DeleteScrapeRunsBeforeId.Value);
    Log($"  ✓ Deleted {affected} run(s) with ID < {opts.DeleteScrapeRunsBeforeId}.");
    return 0;
}

// ── Chain ─────────────────────────────────────────────────────────────────────
ChainConfig? chain = Chains.FindByPlatformSlug(opts.Chain!);
if (chain is null)
{
    Log($"  ✗ Unknown chain slug: '{opts.Chain}'");
    Log($"  Available: {string.Join(", ", Chains.AllPlatformSlugs())}");
    return 1;
}

Log($"  {chain.Name}");
Console.WriteLine();

PipelineOptions pipeOpts = new()
{
    Chain = chain,
    ForceCrawl = opts.ForceCrawl,
    Geocode = opts.Geocode,
    GeocodeRematch = opts.GeocodeRematch,
    Export = opts.Export,
    ExportSince = opts.ExportSince,
    // Tabs intentionally left null: the CLI has no tab picker, so a full run
    // always scrapes every configured tab.
    OutputDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(opts.Db))!, "output"),
};

// Close the maintenance connection — PipelineRunner opens its own.
conn.Dispose();

try
{
    using CancellationTokenSource cts = new();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Log("  ⚠ Cancelling"); };
    await PipelineRunner.RunAsync(opts.Db, pipeOpts, progress, cts.Token, progressCount);
}
catch (OperationCanceledException)
{
    Log("  ✗ Cancelled."); return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"  ✗ Fatal: {ex.Message}"); return 1;
}

// Export is no longer a pipeline step; the CLI performs it directly on request.
if (opts.Export || opts.ExportSince is not null)
{
    Log("Exporting benchmark to Excel");
    string outDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(opts.Db))!, "output");
    using SqliteConnection exportConn = Database.Open(opts.Db);
    string exportPath = ExcelExporter.Export(chain.Name, exportConn, outDir, opts.ExportSince);
    Log($"  ✓ Saved → {exportPath}");
}

return 0;

// ── Console reporting ───────────────────────────────────────────────────────

/// <summary>
/// Serialises all console writes so the live Step-3 geocoding progress bar
/// (drawn in place with a carriage return) never gets interleaved with log
/// lines — mirroring the geocoding bar the Python prototype showed.
/// </summary>
sealed class ConsoleReporter
{
    private readonly object _gate = new();
    private bool _barActive;

    public void Log(string msg)
    {
        lock (_gate)
        {
            if (_barActive) { Console.WriteLine(); _barActive = false; }
            Console.WriteLine(ConsoleColouriser.Colourise(msg));
        }
    }

    public void Bar(StepProgress p)
    {
        // No sensible bar when there's no work, or when output is redirected to a file.
        if (p.Total <= 0 || Console.IsOutputRedirected) return;

        lock (_gate)
        {
            const int width = 28;
            int filled = Math.Clamp((int)Math.Round(width * (double)p.Done / p.Total), 0, width);
            int pct = (int)Math.Round(100.0 * p.Done / p.Total);
            string bar = new string('\u2588', filled) + new string('\u2591', width - filled);
            string line = $"  {p.Phase} [{bar}] {p.Done}/{p.Total} ({pct,3}%)";
            if (ConsoleColouriser.Enabled)
            {
                line = $"\u001b[96m{line}\u001b[0m";   // light cyan, matching the theme
            }

            Console.Write("\r" + line);
            _barActive = true;
            if (p.Done >= p.Total) { Console.WriteLine(); _barActive = false; }
        }
    }
}

/// <summary>Synchronous <see cref="IProgress{T}"/> — reports on the calling thread so console output stays ordered.</summary>
sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
