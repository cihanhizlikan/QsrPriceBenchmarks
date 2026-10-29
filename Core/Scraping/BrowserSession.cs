using Microsoft.Playwright;

namespace QsrPriceBenchmarks.Core.Scraping;

/// <summary>
/// One context + one page on the process's shared headless browser, created with the
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
/// Dispose (via <c>await using</c>) closes the context, and the browser and
/// Playwright driver once no session is left. The single <see cref="Page"/> closes with its
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

    // One browser per process, shared by every session alive at once: chains scraping together each get their
    // own context and page, not their own Chromium (a few hundred MB apiece). Launched by the first session,
    // closed by the last.
    // Held only while the shared browser is launched or closed. Every wait on it is bounded (plan WP-R1): a hung
    // launch or close must fail the run that waits, not stall every chain's next scrape forever. Internal so a
    // test can hold the lock and shorten the bound.
    internal static readonly SemaphoreSlim SharedLock = new(1, 1);
    internal static TimeSpan SharedLockBound { get; set; } = TimeSpan.FromMinutes(2); // Playwright's launch gives up at 30 s
    private static IPlaywright? _sharedPlaywright;
    private static IBrowser? _sharedBrowser;
    private static int _users;

    public IBrowserContext Context { get; }
    public IPage Page { get; }

    private BrowserSession(IBrowserContext context, IPage page)
    {
        Context = context;
        Page = page;
    }

    /// <summary>
    /// Open a ready-to-use page in its own context on the shared headless browser.
    /// </summary>
    /// <param name="viewportHeight">
    /// Viewport height in pixels (width is fixed at 1280). Affects only how
    /// much lazy-loaded content renders before scrolling; defaults to 900.
    /// </param>
    /// <param name="ct">The run's cancellation: Cancel reaches a run waiting for the shared browser.</param>
    public static async Task<BrowserSession> LaunchAsync(int viewportHeight = 900, CancellationToken ct = default)
    {
        IBrowser browser = await AcquireBrowserAsync(ct);
        try
        {
            IBrowserContext context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new ViewportSize { Width = 1280, Height = viewportHeight },
                UserAgent = DefaultUserAgent,
                Locale = "tr-TR",
                IgnoreHTTPSErrors = true, // some chain sites (e.g. usta-pideci) serve a mismatched cert
            });
            IPage page = await context.NewPageAsync();
            return new BrowserSession(context, page);
        }
        catch
        {
            await ReleaseBrowserAsync(); // don't keep the browser alive for a session that never started
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await Context.CloseAsync(); } catch { /* already closing */ }
        await ReleaseBrowserAsync();
    }

    private static async Task<IBrowser> AcquireBrowserAsync(CancellationToken ct)
    {
        if (!await SharedLock.WaitAsync(SharedLockBound, ct))
        {
            throw new TimeoutException(
                $"The shared browser did not become available within {SharedLockBound.TotalSeconds:0} s: another " +
                "run's browser launch or close is stuck. Restart the app if it persists.");
        }
        try
        {
            if (_sharedBrowser is not { IsConnected: true })
            {
                // First user, or the browser died under the sessions using it: start a new one.
                await CloseSharedAsync();
                _sharedBrowser = await LaunchBrowserAsync();
            }
            _users++;
            return _sharedBrowser;
        }
        finally
        {
            SharedLock.Release();
        }
    }

    private static async Task ReleaseBrowserAsync()
    {
        if (!await SharedLock.WaitAsync(SharedLockBound))
        {
            // A run's cleanup must never hang behind a stuck launch or close. Giving up leaves this session
            // counted, so the browser stays open until the process exits — the cost of not waiting forever.
            return;
        }
        try
        {
            if (--_users <= 0)
            {
                _users = 0;
                await CloseSharedAsync();
            }
        }
        finally
        {
            SharedLock.Release();
        }
    }

    private static async Task<IBrowser> LaunchBrowserAsync()
    {
        string? channel = ResolveBrowserChannel();
        if (channel is null)
        {
            EnsureBrowsersPath(); // only need a downloaded/bundled Chromium when NOT using a system channel
        }

        _sharedPlaywright = await Playwright.CreateAsync();
        BrowserTypeLaunchOptions launchOptions = new() { Headless = true };
        if (channel is not null)
        {
            launchOptions.Channel = channel; // drive the installed Edge/Chrome instead of a Chromium download
        }
        return await _sharedPlaywright.Chromium.LaunchAsync(launchOptions);
    }

    private static async Task CloseSharedAsync()
    {
        if (_sharedBrowser is not null)
        {
            try { await _sharedBrowser.CloseAsync(); } catch { /* already gone */ }
            _sharedBrowser = null;
        }
        _sharedPlaywright?.Dispose();
        _sharedPlaywright = null;
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
        string? env = Environment.GetEnvironmentVariable("QSR_BROWSER_CHANNEL");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        try
        {
            string marker = Path.Combine(AppContext.BaseDirectory, "browser-channel.txt");
            if (File.Exists(marker))
            {
                string value = File.ReadAllText(marker).Trim();
                if (value.Length > 0)
                {
                    return value;
                }
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
        {
            return;
        }

        string bundled = Path.Combine(AppContext.BaseDirectory, "ms-playwright");
        if (Directory.Exists(bundled))
        {
            Environment.SetEnvironmentVariable(EnvVar, bundled);
        }
    }
}
