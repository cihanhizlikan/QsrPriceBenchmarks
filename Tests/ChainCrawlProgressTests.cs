using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Core.Scraping;
using QsrPriceBenchmarks.Core.Services;
using QsrPriceBenchmarks.IntegrationTests.Hermetic;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests;

/// <summary>
/// Step 1 reports how far it is, by province (maintainer, 2026-10-29: a Force Crawl "only says Running"). A live
/// crawl is started and cancelled at its first report: the province count is known before the first province.
/// </summary>
[Collection("Playwright")]
[Trait("Category", "Integration")]
public sealed class ChainCrawlProgressTests
{
    [Fact(DisplayName = "Step 1 reports its progress by province from the first one")]
    public async Task Crawl_ReportsProvinceProgress()
    {
        using TempDb db = new();
        ChainConfig chain = ChainRegistry.All.First(c => c.RootUrl is not null);
        using CancellationTokenSource stop = new(TimeSpan.FromMinutes(3));
        FirstReport first = new(stop);

        Task crawl = ChainCrawler.CrawlAsync(chain, db.Conn, ct: stop.Token, progressCount: first);
        StepProgress report = await first.Received.WaitAsync(TimeSpan.FromMinutes(3));
        try
        {
            await crawl;
        }
        catch (Exception stopped) when (stop.IsCancellationRequested)
        {
            _ = stopped; // cancelled at the first report, as intended
        }

        Assert.Equal(ChainCrawler.CrawlPhase, report.Phase);
        Assert.Equal(0, report.Done);
        Assert.True(report.Total > 0, $"[{chain.Name}] reported {report.Total} provinces");
    }

    /// <summary>Keeps the first report and stops the crawl there; synchronous, so nothing races it.</summary>
    private sealed class FirstReport(CancellationTokenSource stop) : IProgress<StepProgress>
    {
        private readonly TaskCompletionSource<StepProgress> _received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<StepProgress> Received
        {
            get
            {
                return _received.Task;
            }
        }

        public void Report(StepProgress value)
        {
            if (_received.TrySetResult(value))
            {
                stop.Cancel();
            }
        }
    }
}
