using System.Text.RegularExpressions;
using QsrPriceBenchmarks.Core.Models;

namespace QsrPriceBenchmarks.IntegrationTests;

internal static class TestHelpers
{
    // TG blocks plain HttpClient requests from the sitemap (returns 403/empty).
    // Use a browser-like UA + Accept headers to avoid it.
    private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
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
    private static readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Returns the first TG restaurant slug for <paramref name="platformSlug"/>
    /// or null when the chain has no TG listings yet.
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
        var sitemap = await GetSitemapAsync();
        if (string.IsNullOrWhiteSpace(sitemap))
            return null;

        var prefix = $"https://www.tiklagelsin.com/restoran/{platformSlug}/";
        var matches = Regex.Matches(sitemap,
            $@"<loc>\s*{Regex.Escape(prefix)}([^/\s<]+)",
            RegexOptions.IgnoreCase);
        return matches.Count >= n ? matches[n - 1].Groups[1].Value.TrimEnd('/') : null;
    }

    /// <summary>Loads (and caches) the TG sitemap XML once for the whole run.</summary>
    private static async Task<string> GetSitemapAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_cachedSitemapXml is null)
            {
                try
                {
                    _cachedSitemapXml = await Http.GetStringAsync(TgSitemapUrl);
                }
                catch
                {
                    _cachedSitemapXml = ""; // each caller handles a missing sitemap gracefully
                }
            }
            return _cachedSitemapXml;
        }
        finally
        {
            _lock.Release();
        }
    }

    public static ChainConfig RequireChain(string platformSlug) =>
        Chains.FindByPlatformSlug(platformSlug)
        ?? throw new InvalidOperationException(
            $"No chain registered with platform slug '{platformSlug}'. " +
            "Check ChainConfig.cs for the correct slug.");
}
