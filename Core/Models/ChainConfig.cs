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
/// <param name="ScrapeTabs">Map of platform display name -> ordered list of TG menu tab names to click through.</param>
public sealed record ChainConfig(
    string Name,
    string? BaseUrl,
    string? RootUrl,
    string SitemapUrl,
    IReadOnlyDictionary<string, string> PlatformSlugs,
    string? ContentSelector,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ScrapeTabs
)
{
    /// <summary>
    /// The single delivery platform this chain is listed on today (Tıkla Gelsin).
    /// Centralises the "exactly one platform" assumption that both Step 2 and the
    /// UI rely on, so there's one place to revisit if a second platform is added.
    /// </summary>
    public string PrimaryPlatform => PlatformSlugs.Keys.First();

    /// <summary>Ordered TG menu tabs for the primary platform (empty if none configured).</summary>
    public IReadOnlyList<string> PrimaryTabs =>
        ScrapeTabs.TryGetValue(PrimaryPlatform, out var tabs) ? tabs : Array.Empty<string>();
}

/// <summary>
/// The full registry of chains this program scrapes. Mirrors CHAINS from the
/// Python prototype, plus four new chains: Arby's, Sbarro, Usta Pideci, Subway.
/// </summary>
public static class Chains
{
    private const string TgSitemap = "https://www.tiklagelsin.com/sitemap/sitemap/restoranlar.xml";

    public static readonly IReadOnlyList<ChainConfig> All = new List<ChainConfig>
    {
        new ChainConfig(
            Name: "Burger King",
            BaseUrl: "https://www.burgerking.com.tr/restoranlar-subeler/",
            RootUrl: "https://www.burgerking.com.tr/restoranlar-subeler",
            SitemapUrl: TgSitemap,
            PlatformSlugs: new Dictionary<string, string> { ["Tıkla Gelsin"] = "burger-king" },
            ContentSelector: "#restaurantsContent",
            ScrapeTabs: new Dictionary<string, IReadOnlyList<string>>
            {
                ["Tıkla Gelsin"] = new List<string>
                {
                    "Popüler Ürünler",
                    "Tıkla Gelsin Özel Menüler",
                    "1-2 Kişilik Fırsatlar",
                    "3-4 Kişilik Fırsatlar",
                    "5 Kişilik Fırsatlar",
                    "Mandalorian & Grogu Lezzetleri",
                    "Menüler",
                    "Çocuk Menüleri",
                    "Burgerler",
                    "Yan Ürünler",
                    "Patates Baharatları",
                    "Tatlılar",
                    "Dondurmalar",
                    "İçecekler",
                    "Sıcak Kahveler",
                    "Soğuk Kahveler",
                },
            }
        ),

        new ChainConfig(
            Name: "Popeyes",
            BaseUrl: "https://www.popeyes.com.tr/subeler/",
            RootUrl: "https://www.popeyes.com.tr/subeler",
            SitemapUrl: TgSitemap,
            PlatformSlugs: new Dictionary<string, string> { ["Tıkla Gelsin"] = "popeyes" },
            ContentSelector: null,
            ScrapeTabs: new Dictionary<string, IReadOnlyList<string>>
            {
                ["Tıkla Gelsin"] = new List<string>
                {
                    "Popüler Ürünler",
                    "Tıkla Gelsin Özel",
                    "Fırsat Menüleri",
                    "Kova Menüleri",
                    "Maxi Menüler",
                    "Kemiksiz Menüler",
                    "Kemikli Menüler",
                    "Sandviç ve Salata Menüleri",
                    "Çocuk Menüleri",
                    "Tekli Tavuklar",
                    "Sandviçler ve Salatalar",
                    "Ek Lezzetler & Yan Ürünler",
                    "Tatlılar & Dondurmalar",
                    "İçecekler",
                    "Kahveler",
                },
            }
        ),

        new ChainConfig(
            Name: "Usta Dönerci",
            BaseUrl: "https://www.ustadonerci.com/tr/restoranlar/",
            RootUrl: "https://www.ustadonerci.com/tr/restoranlar",
            SitemapUrl: TgSitemap,
            PlatformSlugs: new Dictionary<string, string> { ["Tıkla Gelsin"] = "usta-donerci" },
            ContentSelector: null,
            ScrapeTabs: new Dictionary<string, IReadOnlyList<string>>
            {
                ["Tıkla Gelsin"] = new List<string>
                {
                    "Popüler Ürünler",
                    "Tıkla Gelsin Özel",
                    "Fırsat Menüler",
                    "Et Döner Menüler",
                    "Tavuk Döner Menüler",
                    "Köfte Menüler",
                    "Dürüm Menüler",
                    "Bowl Menüler",
                    "Çocuk Menüler",
                    "Et Dönerler",
                    "Tavuk Dönerler",
                    "Köfteler",
                    "Dürümler",
                    "Bowl Ürünler",
                    "Çıtır Lezzetler",
                    "Ek Lezzetler ve Yan Ürünler",
                    "Tatlılar",
                    "İçecekler",
                },
            }
        ),

        // ── New chains ──────────────────────────────────────────────────────
        //
        // All tab lists below are confirmed from the live TG pages. Note that
        // tabs[0] is read from the page's default SSR payload (not clicked), so
        // it must be whatever tab the page renders first — usually
        // "Popüler Ürünler", but "Fırsat Kampanyaları" for Sbarro.

        new ChainConfig(
            Name: "Arby's",
            BaseUrl: "https://www.arbys.com.tr/restoranlar/",
            RootUrl: "https://www.arbys.com.tr/restoranlar",
            SitemapUrl: TgSitemap,
            PlatformSlugs: new Dictionary<string, string> { ["Tıkla Gelsin"] = "arbys" },
            ContentSelector: null,
            ScrapeTabs: new Dictionary<string, IReadOnlyList<string>>
            {
                ["Tıkla Gelsin"] = new List<string>
                {
                    "Popüler Ürünler",
                    "Tıkla Gelsin Özel",
                    "Fırsat Menüler",
                    "Menüler",
                    "Algida Menüleri",
                    "Kids Menüler",
                    "Sandviçler",
                    "Salatalar",
                    "Atıştırmalık Lezzetler",
                    "Tatlı & Dondurmalar",
                    "İçecekler",
                    "Soğuk Kahveler",
                },
            }
        ),

        new ChainConfig(
            Name: "Sbarro",
            BaseUrl: "https://www.sbarro.com.tr/restoranlar-subeler/",
            RootUrl: "https://www.sbarro.com.tr/restoranlar-subeler",
            SitemapUrl: TgSitemap,
            PlatformSlugs: new Dictionary<string, string> { ["Tıkla Gelsin"] = "sbarro" },
            ContentSelector: null,
            // NB: Sbarro is the one chain whose first/default tab is NOT
            // "Popüler Ürünler" — its page leads with "Fırsat Kampanyaları",
            // which is therefore tabs[0] (read from the SSR payload, not clicked).
            ScrapeTabs: new Dictionary<string, IReadOnlyList<string>>
            {
                ["Tıkla Gelsin"] = new List<string>
                {
                    "Fırsat Kampanyaları",
                    "Orta Boy Bol Lezzet Pizzalar (2 Kişilik)",
                    "Orta Boy Favori Pizzalar (2 Kişilik)",
                    "Orta Boy Klasik Pizzalar (2 Kişilik)",
                    "Orta Boy Gurme Pizzalar (2 Kişilik)",
                    "Büyük Boy Klasik Pizzalar (3 Kişilik)",
                    "Büyük Boy Favori Pizzalar (3 Kişilik)",
                    "Büyük Boy Bol Lezzet Pizzalar (3 Kişilik)",
                    "Büyük Boy Gurme Pizzalar (3 Kişilik)",
                    "Küçük Boy Klasik Pizzalar (1 Kişilik)",
                    "Küçük Boy Favori Pizzalar (1 Kişilik)",
                    "Küçük Boy Bol Lezzet Pizzalar (1 Kişilik)",
                    "Küçük Boy Gurme Pizzalar (1 Kişilik)",
                    "XL Boy Klasik Pizzalar (4 Kişilik)",
                    "XL Boy Favori Pizzalar (4 Kişilik)",
                    "XL Boy Bol Lezzet Pizzalar (4 Kişilik)",
                    "XL Boy Gurme Pizzalar (4 Kişilik)",
                    "Pideler ve Lahmacun",
                    "Yan Ürünler",
                    "Tatlılar",
                    "Soğuk İçecekler",
                    "Sıcak İçecekler",
                },
            }
        ),

        new ChainConfig(
            Name: "Usta Pideci",
            BaseUrl: "https://www.ustapideci.com.tr/subeler/",
            RootUrl: "https://www.ustapideci.com.tr/subeler",
            SitemapUrl: TgSitemap,
            PlatformSlugs: new Dictionary<string, string> { ["Tıkla Gelsin"] = "usta-pideci" },
            ContentSelector: null,
            ScrapeTabs: new Dictionary<string, IReadOnlyList<string>>
            {
                ["Tıkla Gelsin"] = new List<string>
                {
                    "Popüler Ürünler",
                    "Kampanyalar",
                    "Pide ve Lahmacun",
                    "Yan Ürünler",
                    "Tatlılar",
                    "Soğuk İçecekler",
                    "Sıcak İçecekler",
                },
            }
        ),

        new ChainConfig(
            Name: "Subway",
            BaseUrl: "https://www.subway.com.tr/restoranlar-subeler/",
            RootUrl: "https://www.subway.com.tr/restoranlar-subeler",
            SitemapUrl: TgSitemap,
            PlatformSlugs: new Dictionary<string, string> { ["Tıkla Gelsin"] = "subway" },
            ContentSelector: null,
            ScrapeTabs: new Dictionary<string, IReadOnlyList<string>>
            {
                ["Tıkla Gelsin"] = new List<string>
                {
                    "Popüler Ürünler",
                    "Fırsat Menüleri",
                    "Şef Serisi Menüler (30 cm.)",
                    "Sub 30 Menüler",
                    "Şef Serisi Menüler (15 cm.)",
                    "Sub 15 Menüler",
                    "Bowl Menüleri",
                    "Dürüm Menüler",
                    "Salatalar",
                    "Protein Serisi",
                    "Şef Serisi Sandviç (30 cm.)",
                    "Sandviçler (30 cm.)",
                    "Şef Serisi Sandviç (15 cm.)",
                    "Sandviçler (15 cm.)",
                    "Bowl Ürünleri",
                    "Dürümler",
                    "Duble Dürümler",
                    "Ek Lezzetler Yan Ürünler",
                    "Kurabiyeler",
                    "Dondurmalar",
                    "İçecekler",
                },
            }
        ),
    };

    /// <summary>
    /// Resolve a chain by its TG platform slug (e.g. "burger-king", "usta-donerci").
    /// This mirrors the Python --chain matching: TG slugs only, no name-derived fallback.
    /// </summary>
    public static ChainConfig? FindByPlatformSlug(string slug)
    {
        var key = slug.Trim();
        return All.FirstOrDefault(c =>
            c.PlatformSlugs.Values.Any(v => string.Equals(v, key, StringComparison.OrdinalIgnoreCase)));
    }

    public static IReadOnlyList<string> AllPlatformSlugs() =>
        All.SelectMany(c => c.PlatformSlugs.Values).ToList();
}
