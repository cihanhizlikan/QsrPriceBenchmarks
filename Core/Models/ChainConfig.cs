namespace QsrPriceBenchmarks.Core.Models;

/// <summary>
/// Static configuration for one QSR chain: its own website (for Step 1) and
/// its listing(s) on delivery platforms (for Step 2).
/// </summary>
/// <param name="Name">Display name stored in the QSR table, e.g. "Burger King".</param>
/// <param name="BaseUrl">
/// Chain website's restaurant-listing base URL (trailing slash), or null for
/// TG-only chains with no separate corporate website to crawl.
/// </param>
/// <param name="RootUrl">
/// Same as BaseUrl but without the trailing slash — used as the Step-1 crawl
/// root and for URL-depth calculations. Null for TG-only chains.
/// </param>
/// <param name="SitemapUrl">TG sitemap XML to harvest this chain's restaurant slugs from.</param>
/// <param name="PlatformSlugs">Map of platform display name -> TG path slug, e.g. {"Tıkla Gelsin": "burger-king"}.</param>
/// <param name="ContentSelector">
/// Optional CSS selector Step 1 waits for after navigation (chain-website specific).
/// Null means rely on the generic cookie-dismiss + scroll routine only.
/// </param>
public sealed record ChainConfig(
    string Name,
    string? BaseUrl,
    string? RootUrl,
    string SitemapUrl,
    IReadOnlyDictionary<string, string> PlatformSlugs,
    string? ContentSelector
)
{
    /// <summary>
    /// The single delivery platform this chain is listed on today (Tıkla Gelsin).
    /// Centralises the "exactly one platform" assumption that both Step 2 and the
    /// UI rely on, so there's one place to revisit if a second platform is added.
    /// </summary>
    public string PrimaryPlatform
    {
        get
        {
            return PlatformSlugs.Keys.First();
        }
    }
}

/// <summary>
/// The full registry of chains this program scrapes. Mirrors CHAINS from the
/// Python prototype, plus four new chains: Arby's, Sbarro, Usta Pideci, Subway.
/// </summary>
public static class ChainRegistry
{
    private const string TgSitemap = "https://www.tiklagelsin.com/sitemap/sitemap/restoranlar.xml";

    public static readonly IReadOnlyList<ChainConfig> All =
    [
        new(
            Name: "Burger King",
            BaseUrl: "https://www.burgerking.com.tr/restoranlar-subeler/",
            RootUrl: "https://www.burgerking.com.tr/restoranlar-subeler",
            SitemapUrl: TgSitemap,
            PlatformSlugs: new Dictionary<string, string> { ["Tıkla Gelsin"] = "burger-king" },
            ContentSelector: "#restaurantsContent"
        ),

        new(
            Name: "Popeyes",
            BaseUrl: "https://www.popeyes.com.tr/subeler/",
            RootUrl: "https://www.popeyes.com.tr/subeler",
            SitemapUrl: TgSitemap,
            PlatformSlugs: new Dictionary<string, string> { ["Tıkla Gelsin"] = "popeyes" },
            ContentSelector: null
        ),

        new(
            Name: "Usta Dönerci",
            BaseUrl: "https://www.ustadonerci.com/tr/restoranlar/",
            RootUrl: "https://www.ustadonerci.com/tr/restoranlar",
            SitemapUrl: TgSitemap,
            PlatformSlugs: new Dictionary<string, string> { ["Tıkla Gelsin"] = "usta-donerci" },
            ContentSelector: null
        ),

        // ── New chains ──────────────────────────────────────────────────────
        //
        // All tab lists below are confirmed from the live TG pages. Note that
        // tabs[0] is read from the page's default SSR payload (not clicked), so
        // it must be whatever tab the page renders first — usually
        // "Popüler Ürünler", but "Fırsat Kampanyaları" for Sbarro.

        new(
            Name: "Arby's",
            BaseUrl: "https://www.arbys.com.tr/restoranlar/",
            RootUrl: "https://www.arbys.com.tr/restoranlar",
            SitemapUrl: TgSitemap,
            PlatformSlugs: new Dictionary<string, string> { ["Tıkla Gelsin"] = "arbys" },
            ContentSelector: null
        ),

        new(
            Name: "Sbarro",
            BaseUrl: "https://www.sbarro.com.tr/restoranlar-subeler/",
            RootUrl: "https://www.sbarro.com.tr/restoranlar-subeler",
            SitemapUrl: TgSitemap,
            PlatformSlugs: new Dictionary<string, string> { ["Tıkla Gelsin"] = "sbarro" },
            ContentSelector: null
        ),

        new(
            Name: "Usta Pideci",
            BaseUrl: "https://www.ustapideci.com.tr/subeler/",
            RootUrl: "https://www.ustapideci.com.tr/subeler",
            SitemapUrl: TgSitemap,
            PlatformSlugs: new Dictionary<string, string> { ["Tıkla Gelsin"] = "usta-pideci" },
            ContentSelector: null
        ),

        new(
            Name: "Subway",
            BaseUrl: "https://www.subway.com.tr/restoranlar-subeler/",
            RootUrl: "https://www.subway.com.tr/restoranlar-subeler",
            SitemapUrl: TgSitemap,
            PlatformSlugs: new Dictionary<string, string> { ["Tıkla Gelsin"] = "subway" },
            ContentSelector: null
        ),
    ];

    /// <summary>
    /// Resolve a chain by its TG platform slug (e.g. "burger-king", "usta-donerci").
    /// This mirrors the Python --chain matching: TG slugs only, no name-derived fallback.
    /// </summary>
    public static ChainConfig? FindByPlatformSlug(string slug)
    {
        string key = slug.Trim();
        return All.FirstOrDefault(c =>
            c.PlatformSlugs.Values.Any(v => string.Equals(v, key, StringComparison.OrdinalIgnoreCase)));
    }

    public static IReadOnlyList<string> AllPlatformSlugs()
    {
        return [.. All.SelectMany(c => c.PlatformSlugs.Values)];
    }
}
