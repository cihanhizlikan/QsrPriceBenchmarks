using System.Diagnostics;
using Microsoft.Playwright;
using QsrPriceBenchmarks.Core.Scraping;
using QsrPriceBenchmarks.IntegrationTests.Fixtures;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests;

/// <summary>
/// WP-R2, 2026-11-13: Cancel reaches a geocode that is waiting on Google. Playwright calls take no token, so the
/// search would otherwise run out its navigation timeout (20 s), up to three times. The page's requests are held by
/// a route that never answers, so nothing reaches Google and the wait is the one a slow Google causes. It needs
/// the test browser, so it runs with the live half.
/// </summary>
[Collection("Playwright")]
[Trait("Category", "Integration")]
public sealed class GeocodeCancelTests(PlaywrightFixture pw)
{
    [Fact(DisplayName = "Cancel ends a geocode waiting on Google at once, not at its timeout")]
    public async Task Cancel_EndsAPendingGeocode()
    {
        (IBrowserContext ctx, IPage page) = await pw.NewPageAsync();
        await using IBrowserContext _ = ctx;
        using CancellationTokenSource held = new();
        await page.RouteAsync("**/*", async route =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, held.Token);
            }
            catch (OperationCanceledException)
            {
                // The test is over; the request is abandoned with its page.
            }
        });

        using CancellationTokenSource cancel = new();
        Task<Geocoder.GeocodeResult> geocode = Geocoder.GeocodeDetailedAsync(page, "Kızılay, Ankara", cancel.Token);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(geocode.IsCompleted, "the geocode must still be waiting on the held request when Cancel comes");

        Stopwatch clock = Stopwatch.StartNew();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => geocode.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"the geocode ended {clock.Elapsed} after Cancel");

        await held.CancelAsync();
    }
}
