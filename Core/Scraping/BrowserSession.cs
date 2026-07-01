using Microsoft.Playwright;

namespace QsrPriceBenchmarks.Core.Scraping;

/// <summary>
/// Owns a headless browser + one context + one page, created with the
/// project-standard viewport, Turkish locale, and desktop Chrome user-agent.
/// Centralises the launch boilerplate that previously lived in
/// <see cref="ChainCrawler"/>, <see cref="TgScraper"/>, and
/// <see cref="GeocodeAndMatchStep"/>.
///
/// "Browser" here is the automation engine Playwright drives, not a browser the
/// person clicks around in. By default that is the Chromium build Playwright
/// manages (downloaded to a per-user cache, or installer-bundled into an
/// "ms-playwright" folder beside the exe). Alternatively, set a channel — see
/// <see cref="ResolveBrowserChannel"/> — to drive an already-installed Edge or
/// Chrome instead, which avoids shipping/downloading Chromium at all.
///
/// Dispose (via <c>await using</c>) closes the context and browser and frees
/// the Playwright driver. The single <see cref="Page"/> closes with its
/// context, so callers no longer call <c>page.CloseAsync()</c> explicitly.
/// </summary>
public sealed class BrowserSession : IAsyncDisposable
{
    /// <summary>
    /// Desktop Chrome UA presented by every scraper request. TG and several
    /// chain sites return 403/empty bodies to non-browser agents, so this is
    /// load-bearing, not cosmetic.
    /// </summary>
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;

    public IBrowserContext Context { get; }
    public IPage Page { get; }

    private BrowserSession(IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage page)
    {
        _playwright = playwright;
        _browser = browser;
        Context = context;
        Page = page;
    }

    /// <summary>
    /// Launch a headless browser and open a ready-to-use page.
    /// </summary>
    /// <param name="viewportHeight">
    /// Viewport height in pixels (width is fixed at 1280). Affects only how
    /// much lazy-loaded content renders before scrolling; defaults to 900.
    /// </param>
    public static async Task<BrowserSession> LaunchAsync(int viewportHeight = 900)
    {
        var channel = ResolveBrowserChannel();
        if (channel is null)
            EnsureBrowsersPath(); // only need a downloaded/bundled Chromium when NOT using a system channel

        var playwright = await Playwright.CreateAsync();
        try
        {
            var launchOptions = new BrowserTypeLaunchOptions { Headless = true };
            if (channel is not null)
                launchOptions.Channel = channel; // drive the installed Edge/Chrome instead of a Chromium download

            var browser = await playwright.Chromium.LaunchAsync(launchOptions);
            var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = viewportHeight },
                UserAgent = DefaultUserAgent,
                Locale = "tr-TR",
                IgnoreHTTPSErrors = true, // some chain sites (e.g. usta-pideci) serve a mismatched cert
            });
            var page = await context.NewPageAsync();
            return new BrowserSession(playwright, browser, context, page);
        }
        catch
        {
            // Don't leak the driver process if context/page creation fails.
            playwright.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Context.CloseAsync(); } catch { /* already closing */ }
        try { await _browser.CloseAsync(); } catch { /* already closing */ }
        _playwright.Dispose();
    }

    /// <summary>
    /// Optional Playwright "channel" naming an already-installed browser to drive
    /// — "msedge" (preinstalled on Windows 10/11) or "chrome" — instead of a
    /// Chromium build Playwright downloads. Resolved from the
    /// QSR_BROWSER_CHANNEL environment variable, or from a "browser-channel.txt"
    /// file shipped next to the executable (what the -UseSystemBrowser installer
    /// build writes). Returns null when neither is present, in which case
    /// Playwright uses its own Chromium (bundled, or the per-user cache).
    /// </summary>
    private static string? ResolveBrowserChannel()
    {
        var env = Environment.GetEnvironmentVariable("QSR_BROWSER_CHANNEL");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim();

        try
        {
            var marker = Path.Combine(AppContext.BaseDirectory, "browser-channel.txt");
            if (File.Exists(marker))
            {
                var value = File.ReadAllText(marker).Trim();
                if (value.Length > 0)
                    return value;
            }
        }
        catch { /* unreadable marker — fall back to Chromium */ }

        return null;
    }

    /// <summary>
    /// When the app ships its own Chromium (installer-bundled into an
    /// "ms-playwright" folder beside the executable), point Playwright at it so
    /// no per-machine "playwright install" download is needed on the target PC.
    /// Does nothing if the user already set PLAYWRIGHT_BROWSERS_PATH or if no
    /// bundled folder exists (e.g. a normal dev build), so developers keep using
    /// the default per-user browser cache.
    /// </summary>
    private static void EnsureBrowsersPath()
    {
        const string EnvVar = "PLAYWRIGHT_BROWSERS_PATH";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(EnvVar)))
            return;

        var bundled = Path.Combine(AppContext.BaseDirectory, "ms-playwright");
        if (Directory.Exists(bundled))
            Environment.SetEnvironmentVariable(EnvVar, bundled);
    }
}
