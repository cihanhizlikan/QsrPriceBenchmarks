using AngleSharp.Dom;

namespace QsrPriceBenchmarks.Core.Scraping;

/// <summary>
/// URL resolution and depth-based same-tree link extraction for chain
/// websites. Two important lessons baked in from the Python prototype's
/// development:
///   1. Some sites (e.g. Usta Dönerci) declare a &lt;base href="..."&gt; tag and
///      use relative hrefs against it, not against the current page URL.
///      Resolving against the page URL instead produces doubled path
///      segments (e.g. ".../adana/restoranlar/adana/yuregir/").
///   2. Depth-filtering by checking only the first path segment of the root
///      URL is not enough when the root has a language prefix
///      (e.g. "/tr/restoranlar") — any "/tr/xxx/yyy" link at the same depth
///      would incorrectly match. The full root path must be a prefix.
/// </summary>
public static class UrlUtils
{
    /// <summary>
    /// Resolve <paramref name="href"/> against the page's &lt;base&gt; tag if
    /// present, otherwise against <paramref name="pageUrl"/>.
    /// </summary>
    public static string AbsUrl(IDocument document, string href, string pageUrl)
    {
        var baseTag = document.QuerySelector("base[href]");
        var resolveBase = baseTag?.GetAttribute("href");
        var effectiveBase = !string.IsNullOrWhiteSpace(resolveBase) ? resolveBase! : pageUrl;
        return new Uri(new Uri(effectiveBase), href).ToString();
    }

    /// <summary>
    /// Extract same-domain links exactly <paramref name="depthOffset"/>
    /// levels deeper than <paramref name="baseUrl"/>. Used for both province
    /// links (offset 1 from chain root) and district links
    /// (offset 1 from a province page).
    /// </summary>
    public static List<string> ParseDepthLinks(
        IDocument document, string baseUrl, int depthOffset = 1)
    {
        var baseUri = new Uri(baseUrl);
        var baseParts = SplitPath(baseUri.AbsolutePath);
        var target = baseParts.Count + depthOffset;
        var baseDomain = baseUri.Host;

        var seen = new HashSet<string>();
        var results = new List<string>();

        foreach (var a in document.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
                continue;

            string absHref;
            try
            {
                absHref = AbsUrl(document, href, baseUrl);
            }
            catch (UriFormatException)
            {
                continue;
            }

            var parsed = new Uri(absHref);
            if (!string.Equals(parsed.Host, baseDomain, StringComparison.OrdinalIgnoreCase))
                continue;

            var pathParts = SplitPath(parsed.AbsolutePath);

            // Reject links that don't start with ALL base path segments —
            // checking only the first segment is insufficient when the root
            // has a language prefix (e.g. "/tr/restoranlar").
            if (pathParts.Count < baseParts.Count ||
                !pathParts.Take(baseParts.Count).SequenceEqual(baseParts, StringComparer.OrdinalIgnoreCase))
                continue;

            if (pathParts.Count != target)
                continue;

            var normalised = NormaliseTrailingSlash(absHref);
            if (seen.Add(normalised))
                results.Add(normalised);
        }

        return results;
    }

    /// <summary>
    /// Split a URL path into non-empty segments, e.g. "/tr/restoranlar/" -&gt;
    /// ["tr", "restoranlar"].
    /// </summary>
    public static List<string> SplitPath(string path) =>
        path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string NormaliseTrailingSlash(string url) =>
        url.EndsWith('/') ? url : url + "/";

    /// <summary>
    /// Extract (province, district, slug) from a restaurant URL relative to the
    /// chain's root URL. Accepts both "province/district/slug" (3 path segments
    /// after root) and "province/slug" (2 segments — province used as both province and
    /// district, for chains with a flat hierarchy).
    /// </summary>
    public static (string Province, string District, string Slug) UrlToParts(string url, string rootUrl)
    {
        var rootParts = SplitPath(new Uri(rootUrl).AbsolutePath);
        var urlParts = SplitPath(new Uri(url).AbsolutePath);

        if (urlParts.Count < rootParts.Count ||
            !urlParts.Take(rootParts.Count).SequenceEqual(rootParts, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"URL is not under the chain root: '{url}'");
        }

        var relative = urlParts.Skip(rootParts.Count).ToList();

        return relative.Count switch
        {
            2 => (relative[0], relative[0], relative[1]),
            3 => (relative[0], relative[1], relative[2]),
            _ => throw new ArgumentException(
                $"Unexpected URL structure ({relative.Count} parts after root): {url}"),
        };
    }

}
