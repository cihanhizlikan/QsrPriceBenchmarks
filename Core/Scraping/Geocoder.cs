using System.Text.RegularExpressions;
using System.Web;
using Microsoft.Playwright;
using QsrPriceBenchmarks.Core.Models;

namespace QsrPriceBenchmarks.Core.Scraping;

/// <summary>
/// Geocodes a free-text address by driving Google Maps in a headless
/// browser: navigate to a search URL, wait for the redirect to a
/// "/@lat,lon,zoom" URL, and parse the coordinates out of it. There is no
/// official free API for this at the volume this program needs, so
/// browser automation is used instead — same approach as the prototype.
/// </summary>
public static partial class Geocoder
{
    [GeneratedRegex(@"/@(-?\d+\.\d+),(-?\d+\.\d+)")]
    private static partial Regex CoordsInUrlRegex();

    /// <summary>Outcome of a single geocode, used to gauge whether Google Maps is keeping up.</summary>
    public enum GeocodeOutcome
    {
        Found,     // coordinates parsed
        NoMatch,   // page responded but produced no single-result redirect
        TimedOut,  // navigation did not complete in time — Google slow/blocking (strain)
        Error      // browser/page failure (strain)
    }

    public readonly record struct GeocodeResult(GeoPoint? Point, GeocodeOutcome Outcome);

    /// <summary>
    /// Like <see cref="GeocodeAsync"/> but reports the outcome so callers can
    /// distinguish a genuine "no match" from Google failing to keep up
    /// (a timeout/error) — the signal the adaptive geocode step uses to raise or
    /// lower its parallelism. Retries transient strain (timeout/error) a few
    /// times, and dismisses Google's consent interstitial which otherwise blocks
    /// the "/@lat,lon" redirect and shows up as a flaky ~50% failure rate.
    /// </summary>
    public static async Task<GeocodeResult> GeocodeDetailedAsync(IPage page, string address)
    {
        GeocodeResult result = new(null, GeocodeOutcome.Error);
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            result = await GeocodeOnceAsync(page, address);
            if (result.Outcome is GeocodeOutcome.Found or GeocodeOutcome.NoMatch)
            {
                return result;  // a definitive answer — don't waste retries
            }
            // TimedOut / Error → Google is straining; brief backoff then retry.
            await page.WaitForTimeoutAsync(800 * attempt);
        }
        return result;
    }

    private static async Task<GeocodeResult> GeocodeOnceAsync(IPage page, string address)
    {
        string query = HttpUtility.UrlEncode(address);
        string searchUrl = $"https://www.google.com/maps/search/{query}";

        try
        {
            await page.GotoAsync(searchUrl, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = 20_000,
            });
        }
        catch (TimeoutException)
        {
            return new GeocodeResult(null, GeocodeOutcome.TimedOut);
        }
        catch (PlaywrightException)
        {
            return new GeocodeResult(null, GeocodeOutcome.Error);
        }

        // Google shows a consent wall (consent.google.com) in TR/EU that blocks
        // the redirect to "/@lat,lon". Accept it so the search can resolve.
        await DismissGoogleConsentAsync(page);

        try
        {
            await page.WaitForFunctionAsync(
                "() => window.location.href.includes('/@')",
                options: new PageWaitForFunctionOptions { Timeout = 12_000 });
        }
        catch (TimeoutException)
        {
            return new GeocodeResult(null, GeocodeOutcome.NoMatch);
        }
        catch (PlaywrightException)
        {
            return new GeocodeResult(null, GeocodeOutcome.Error);
        }

        Match m = CoordsInUrlRegex().Match(page.Url);
        if (!m.Success)
        {
            return new GeocodeResult(null, GeocodeOutcome.NoMatch);
        }

        double lat = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        double lon = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        return new GeocodeResult(new GeoPoint(lat, lon), GeocodeOutcome.Found);
    }

    /// <summary>
    /// Best-effort dismissal of Google's cookie-consent interstitial. Clicks an
    /// "accept all"/"kabul et" style control if present. No-ops when there is no
    /// consent wall (every subsequent search in the same context is then clear).
    /// </summary>
    private static async Task DismissGoogleConsentAsync(IPage page)
    {
        try
        {
            bool clicked = await page.EvaluateAsync<bool>("""
                () => {
                    const norm = s => (s || '').replace(/\s+/g, ' ').trim().toLowerCase();
                    const accept = ['tümünü kabul et','hepsini kabul et','kabul et',
                        'accept all','i agree','agree','accept'];
                    const els = [...document.querySelectorAll(
                        'button, [role="button"], a, input[type="submit"]')];
                    for (const el of els) {
                        const t = norm(el.innerText || el.textContent || el.value);
                        if (t && accept.some(a => t === a || t.startsWith(a))) {
                            el.click();
                            return true;
                        }
                    }
                    return false;
                }
                """);
            if (clicked)
            {
                await page.WaitForTimeoutAsync(700);
            }
        }
        catch
        {
            // Non-fatal — absence of a consent wall is the normal case.
        }
    }

    public static async Task<GeoPoint?> GeocodeAsync(IPage page, string address)
        => (await GeocodeDetailedAsync(page, address)).Point;
}
