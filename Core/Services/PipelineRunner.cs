using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Scraping;

namespace QsrPriceBenchmarks.Core.Services;

/// <summary>
/// The single entry point that both the CLI and the UI call to execute the
/// QSR price-benchmark pipeline. Accepts an <see cref="IProgress{T}"/> so
/// that progress messages are delivered appropriately to each consumer:
///   CLI  — coloured Console.WriteLine via a Progress&lt;string&gt; wrapper
///   WPF  — appended to an ObservableCollection via the captured UI
///          SynchronizationContext (Progress&lt;T&gt; handles the dispatch)
/// Returns the path of the exported Excel file when an export step ran,
/// otherwise null.
/// </summary>
public static class PipelineRunner
{
    public static async Task<string?> RunAsync(
        string dbPath,
        PipelineOptions opts,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        IProgress<StepProgress>? progressCount = null,
        IProgress<int>? onLocationsChanged = null)
    {
        using var conn = Db.Database.Open(dbPath);
        var chain    = opts.Chain;
        var qsrId    = Repository.QsrId(conn, chain.Name);
        string? outputPath = null;

        void Log(string msg) => progress?.Report(msg);

        if (opts.IsSelective)
        {
            if (opts.ForceCrawl)
            {
                if (chain.RootUrl is null)
                    Log($"[Step 1] SKIP — {chain.Name} has no chain website (TG-only).");
                else
                {
                    Log($"[Step 1] FORCE — re-crawling {chain.RootUrl}");
                    await ChainCrawler.CrawlAsync(chain, conn, progress, ct, onLocationsChanged);
                }
            }

            if (opts.Geocode || opts.GeocodeRematch)
            {
                Log(opts.GeocodeRematch
                    ? $"[Step 3] Rematching from scratch for {chain.Name}"
                    : $"[Step 3] Geocoding gaps and matching for {chain.Name}");
                await GeocodeAndMatchStep.RunAsync(conn, qsrId, opts.GeocodeRematch, progress, ct, progressCount);
            }
            // Export is not a pipeline step — use the Scrape Runs "Export" button
            // (UI) or the post-run export (CLI --export).
        }
        else
        {
            // ── Normal full run: Steps 1 -> 2 -> 3 ───────────────────────────
            if (chain.RootUrl is null)
            {
                Log($"[Step 1] SKIP — {chain.Name} has no chain website (TG-only).");
            }
            else if (Repository.CountLocations(conn, qsrId) > 0)
            {
                Log($"[Step 1] SKIP — {chain.Name} already has locations; use Force Crawl to rescan.");
            }
            else
            {
                Log($"[Step 1] Crawling {chain.RootUrl}");
                await ChainCrawler.CrawlAsync(chain, conn, progress, ct, onLocationsChanged);
            }

            ct.ThrowIfCancellationRequested();

            Log($"[Step 2] Starting new price round for {chain.Name}");
            await TgScraper.ScrapeAsync(chain, conn, progress, ct, opts.Tabs);

            ct.ThrowIfCancellationRequested();

            Log($"[Step 3] Geocoding gaps and matching for {chain.Name}");
            await GeocodeAndMatchStep.RunAsync(conn, qsrId, rematch: false, progress, ct, progressCount);
            // Export is no longer part of the pipeline — use the "Export" button
            // on the Scrape Runs tab to produce the Excel benchmark on demand.
        }

        return outputPath;
    }

    /// <summary>
    /// Resume an errored/interrupted scrape run: continue Step 2 for the platform
    /// locations still missing from that run, then re-run Step 3 (geocode/match).
    /// The run is finalized when scraping completes (see <see cref="TgScraper"/>).
    /// </summary>
    public static async Task ResumeRunAsync(
        string dbPath, Models.ChainConfig chain, long runId,
        IProgress<string>? progress = null, CancellationToken ct = default,
        IReadOnlyCollection<string>? tabs = null,
        IProgress<StepProgress>? progressCount = null)
    {
        using SqliteConnection conn = Db.Database.Open(dbPath);
        long qsrId = Repository.QsrId(conn, chain.Name);

        progress?.Report($"[Resume] Continuing run {runId} for {chain.Name}");
        await TgScraper.ScrapeAsync(chain, conn, progress, ct, tabs, resumeRunId: runId);

        ct.ThrowIfCancellationRequested();

        progress?.Report($"[Step 3] Geocoding gaps and matching for {chain.Name}");
        await GeocodeAndMatchStep.RunAsync(conn, qsrId, rematch: false, progress, ct, progressCount);
    }
}
