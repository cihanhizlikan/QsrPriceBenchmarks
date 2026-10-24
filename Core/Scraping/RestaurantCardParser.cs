using System.Net;
using AngleSharp.Dom;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Core.Util;

namespace QsrPriceBenchmarks.Core.Scraping;

/// <summary>
/// Extracts restaurant cards (url, name, address) from a chain website page.
/// Different chains structure their restaurant listing markup differently,
/// so four strategies are tried in order; the first one that yields a name
/// wins for that particular &lt;a&gt; element.
/// </summary>
public static class RestaurantCardParser
{
    public static List<RestaurantCard> Parse(IDocument document, string baseUrl)
    {
        List<RestaurantCard> results = [];
        HashSet<string> seen = [];

        foreach (IElement a in document.QuerySelectorAll("a[href]"))
        {
            string? href = a.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            string absHref;
            try
            {
                absHref = UrlUtils.AbsUrl(document, href, baseUrl);
            }
            catch (UriFormatException)
            {
                continue;
            }

            if (!seen.Add(absHref))
            {
                continue;
            }

            // The strategies run in order and the first one that yields a NAME
            // wins, with its own address. When none does, the link is a bare
            // navigation link (e.g. a district index link) and is recorded with
            // name = null, so callers can tell "a link to sub-crawl" from "a
            // restaurant".
            (string? name, string? address) = HeadingCard(a);
            if (name is null)
            {
                (name, address) = IdOrClassCard(a);
            }
            if (name is null)
            {
                (name, address) = ContainerCard(a);
            }

            // ── Final guard ──────────────────────────────────────────────────
            // Never store junk a strategy may have picked up. A lone "+" (an
            // expand toggle), a divider, or a bare number is not a real name —
            // null it so callers treat the link as navigation, not a restaurant.
            // A nameless card carries no address either.
            if (!TextNormalization.IsPlausibleName(name))
            {
                name = null;
            }
            if (name is null || !TextNormalization.IsPlausibleAddress(address))
            {
                address = null;
            }

            results.Add(new RestaurantCard(absHref, name, address));
        }

        return results;
    }

    /// <summary>
    /// Strategy 1: <c>&lt;a&gt;&lt;h3&gt;NAME&lt;/h3&gt;&lt;p&gt;ADDRESS&lt;/p&gt;&lt;/a&gt;</c>
    /// (Usta Dönerci style) — the &lt;a&gt; itself is the card container. Must be
    /// tried BEFORE <see cref="IdOrClassCard"/>, whose address-class match would
    /// otherwise hit the &lt;a class="restaurant-address"&gt; itself and return the
    /// whole card's concatenated text.
    /// </summary>
    private static (string? Name, string? Address) HeadingCard(IElement a)
    {
        IElement? h3 = a.QuerySelector("h3");
        if (h3 is null || a.QuerySelector("p") is null)
        {
            return (null, null);
        }

        // The first <p> that actually looks like an address. Some chains (e.g.
        // Usta Dönerci) put a numeric badge/count in an earlier <p>; taking the
        // first <p> blindly stored "1" as the address. Null if none are address-like.
        string? address = a.QuerySelectorAll("p")
            .Select(p => Clean(p.TextContent))
            .FirstOrDefault(TextNormalization.IsPlausibleAddress);
        return (Clean(h3.TextContent), address);
    }

    /// <summary>
    /// Strategy 2: <c>id="restaurantCardTitle"</c> / <c>"restaurantCardAddress"</c>
    /// (BK style), or any descendant whose id or class says title/name and
    /// address/addr.
    /// </summary>
    private static (string? Name, string? Address) IdOrClassCard(IElement a)
    {
        IElement? nameEl = a.QuerySelector("#restaurantCardTitle")
            ?? a.QuerySelectorAll("*").FirstOrDefault(e =>
                ContainsIgnoreCase(e.GetAttribute("id"), "title") ||
                ContainsIgnoreCase(e.GetAttribute("id"), "name") ||
                ContainsIgnoreCase(e.ClassName, "title") ||
                ContainsIgnoreCase(e.ClassName, "name"));
        if (nameEl is null)
        {
            return (null, null);
        }

        IElement? addrEl = a.QuerySelector("#restaurantCardAddress")
            ?? a.QuerySelectorAll("*").FirstOrDefault(e =>
                ContainsIgnoreCase(e.GetAttribute("id"), "address") ||
                ContainsIgnoreCase(e.GetAttribute("id"), "addr") ||
                ContainsIgnoreCase(e.ClassName, "address") ||
                ContainsIgnoreCase(e.ClassName, "addr"));
        return (Clean(nameEl.TextContent), addrEl is not null ? Clean(addrEl.TextContent) : null);
    }

    /// <summary>
    /// Strategy 3: <c>&lt;a&gt;NAME&lt;/a&gt;</c> + a sibling &lt;p&gt;/&lt;span&gt; in the
    /// enclosing container (Popeyes style) — the &lt;a&gt; text IS the name, and the
    /// address is a sibling inside the nearest li/article/div.restaurant wrapper.
    /// </summary>
    private static (string? Name, string? Address) ContainerCard(IElement a)
    {
        string? aText = Clean(a.TextContent);
        if (string.IsNullOrEmpty(aText))
        {
            return (null, null);
        }

        IElement? container = a.Closest("li, article, div.restaurant") ?? a.ParentElement;
        IElement? addrEl = container?.QuerySelectorAll("p, span")
            .FirstOrDefault(e => !string.Equals(Clean(e.TextContent), aText, StringComparison.Ordinal));
        return (aText, addrEl is not null ? Clean(addrEl.TextContent) : null);
    }

    private static bool ContainsIgnoreCase(string? haystack, string needle)
    {
        return !string.IsNullOrEmpty(haystack) &&
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        string decoded = WebUtility.HtmlDecode(text);
        string collapsed = string.Join(' ', decoded.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? null : collapsed;
    }
}
