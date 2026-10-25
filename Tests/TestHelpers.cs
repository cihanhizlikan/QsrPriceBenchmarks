using System.Text.RegularExpressions;
using QsrPriceBenchmarks.Core.Models;

namespace QsrPriceBenchmarks.IntegrationTests;

internal static class TestHelpers
{
    // TG blocks plain HttpClient requests from the sitemap (returns 403/empty).
    // Use a browser-like UA + Accept headers to avoid it.
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    {
        DefaultRequestHeaders =
        {
            { "User-Agent",
              "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
              "AppleWebKit/537.36 (KHTML, like Gecko) " +
              "Chrome/124.0.0.0 Safari/537.36" },
            { "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8" },
            { "Accept-Language", "tr-TR,tr;q=0.9,en-US;q=0.8,en;q=0.7" },
        },
        Timeout = TimeSpan.FromSeconds(45),
    };

    private const string TgSitemapUrl =
        "https://www.tiklagelsin.com/sitemap/sitemap/restoranlar.xml";

    private static string? _cachedSitemapXml;
    // Why the one fetch failed, kept so 28 cases fail fast on the same reason
    // instead of each waiting out the 45 s timeout again.
    private static string? _sitemapFailure;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Returns the first TG restaurant slug for <paramref name="platformSlug"/>
    /// or null when the chain has no TG listings yet. Throws when the sitemap
    /// itself could not be fetched — see <see cref="GetSitemapAsync"/>.
    /// </summary>
    public static Task<string?> GetFirstSlugFromSitemapAsync(string platformSlug) =>
        GetNthSlugFromSitemapAsync(platformSlug, 1);

    /// <summary>
    /// Returns the <paramref name="n"/>-th (1-based) TG restaurant slug for
    /// <paramref name="platformSlug"/> from the cached sitemap, or null when
    /// there are fewer than <paramref name="n"/> listings. Used to sample past
    /// an inactive/empty first slug without re-downloading the sitemap.
    /// </summary>
    public static async Task<string?> GetNthSlugFromSitemapAsync(string platformSlug, int n)
    {
        string sitemap = await GetSitemapAsync();
        string prefix = $"https://www.tiklagelsin.com/restoran/{platformSlug}/";
        MatchCollection matches = Regex.Matches(sitemap,
            $@"<loc>\s*{Regex.Escape(prefix)}([^/\s<]+)",
            RegexOptions.IgnoreCase);
        return matches.Count >= n ? matches[n - 1].Groups[1].Value.TrimEnd('/') : null;
    }

    /// <summary>
    /// Loads (and caches) the TG sitemap XML once for the whole run. A failed
    /// fetch — an HTTP error status, an empty body (how TG answers a client it
    /// blocks), a network error or the timeout — THROWS, every time it is asked
    /// for: an absent sitemap would otherwise read exactly like "this chain is
    /// not on TG", and every TG test would pass having asserted nothing.
    /// </summary>
    private static async Task<string> GetSitemapAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_cachedSitemapXml is null && _sitemapFailure is null)
            {
                try
                {
                    using HttpResponseMessage response = await Http.GetAsync(TgSitemapUrl);
                    string body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        _sitemapFailure = $"HTTP {(int)response.StatusCode} {response.StatusCode}";
                    }
                    else if (string.IsNullOrWhiteSpace(body))
                    {
                        _sitemapFailure = $"HTTP {(int)response.StatusCode} with an empty body";
                    }
                    else
                    {
                        _cachedSitemapXml = body;
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    _sitemapFailure = $"{ex.GetType().Name}: {ex.Message}";
                }
            }

            if (_cachedSitemapXml is null)
            {
                throw new InvalidOperationException(
                    $"The Tıkla Gelsin sitemap could not be fetched ({_sitemapFailure}) from {TgSitemapUrl}. " +
                    "This is the site or the network, not the scraper — but without the sitemap a chain " +
                    "that is not on TG cannot be told apart from TG being unreachable, so the test fails.");
            }
            return _cachedSitemapXml;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static ChainConfig RequireChain(string platformSlug) =>
        ChainRegistry.FindByPlatformSlug(platformSlug)
        ?? throw new InvalidOperationException(
            $"No chain registered with platform slug '{platformSlug}'. " +
            "Check ChainConfig.cs for the correct slug.");
}
