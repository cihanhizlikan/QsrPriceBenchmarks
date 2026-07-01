using Xunit;
using Microsoft.Playwright;
using QsrPriceBenchmarks.Core.Scraping;

namespace QsrPriceBenchmarks.IntegrationTests.Fixtures;

/// <summary>
/// Creates one headless Chromium browser and holds it for the lifetime of the
/// test collection. Each test creates its own <see cref="IBrowserContext"/> +
/// <see cref="IPage"/> so pages cannot interfere with each other.
/// Browser launch is the expensive operation (~2–3 s); context/page creation
/// is cheap.
///
/// The matching browser build is installed automatically (see
/// <see cref="InitializeAsync"/>) — Playwright pins a browser revision per
/// package version, so a Microsoft.Playwright update would otherwise leave the
/// previously-installed browser stale and every test failing with
/// "Executable doesn't exist". No manual `playwright install` step is needed.
/// </summary>
public sealed class PlaywrightFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;
    public  IBrowser?    Browser { get; private set; }

    public async Task InitializeAsync()
    {
        // Idempotent: a fast no-op once the correct browser build is cached,
        // and the one-time download (~150 MB) after a Playwright version bump.
        // Without this, a stale/missing browser fails the whole collection.
        var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
        if (exitCode != 0)
            throw new InvalidOperationException(
                $"Playwright browser install failed (exit code {exitCode}). " +
                "From a built test project you can also run: " +
                "pwsh bin/Debug/net10.0/playwright.ps1 install chromium");

        _playwright = await Playwright.CreateAsync();
        Browser     = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
        });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.CloseAsync();
        _playwright?.Dispose();
    }

    /// <summary>
    /// Open a fresh browser context + page with Turkish locale and a realistic
    /// user-agent string. The caller is responsible for closing the context.
    /// </summary>
    public async Task<(IBrowserContext Context, IPage Page)> NewPageAsync()
    {
        var ctx  = await Browser!.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            UserAgent    = BrowserSession.DefaultUserAgent,
            Locale = "tr-TR",
            IgnoreHTTPSErrors = true,
        });
        var page = await ctx.NewPageAsync();
        return (ctx, page);
    }
}

/// <summary>Marks tests as members of the sequential "Playwright" collection.</summary>
[CollectionDefinition("Playwright")]
public sealed class PlaywrightCollectionDefinition : ICollectionFixture<PlaywrightFixture> { }
