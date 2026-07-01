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
        var results = new List<RestaurantCard>();
        var seen = new HashSet<string>();

        foreach (var a in document.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
                continue;

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
                continue;

            string? name = null;
            string? address = null;

            // ── Strategy 1: <a><h3>NAME</h3><p>ADDRESS</p></a> (Usta Dönerci style) ──
            // The <a> itself is the card container; h3/p are direct children.
            // Must be tried BEFORE Strategy 2 — Strategy 2's address-class
            // regex would otherwise match the <a class="restaurant-address">
            // element itself and return the whole card's concatenated text.
            var h3 = a.QuerySelector("h3");
            var pDirect = a.QuerySelector("p");
            if (h3 is not null && pDirect is not null)
            {
                name = Clean(h3.TextContent);
                // Pick the first <p> that actually looks like an address. Some
                // chains (e.g. Usta Dönerci) put a numeric badge/count in an
                // earlier <p>; selecting the first <p> blindly stored "1" as the
                // address. Skip <p>s that aren't address-like; null if none are.
                address = a.QuerySelectorAll("p")
                    .Select(p => Clean(p.TextContent))
                    .FirstOrDefault(TextNormalization.IsPlausibleAddress);
            }

            // ── Strategy 2: id="restaurantCardTitle"/"restaurantCardAddress" (BK style) ──
            if (name is null)
            {
                var nameEl = a.QuerySelector("#restaurantCardTitle")
                    ?? a.QuerySelectorAll("*").FirstOrDefault(e =>
                        ContainsIgnoreCase(e.GetAttribute("id"), "title") ||
                        ContainsIgnoreCase(e.GetAttribute("id"), "name") ||
                        ContainsIgnoreCase(e.ClassName, "title") ||
                        ContainsIgnoreCase(e.ClassName, "name"));
                var addrEl = a.QuerySelector("#restaurantCardAddress")
                    ?? a.QuerySelectorAll("*").FirstOrDefault(e =>
                        ContainsIgnoreCase(e.GetAttribute("id"), "address") ||
                        ContainsIgnoreCase(e.GetAttribute("id"), "addr") ||
                        ContainsIgnoreCase(e.ClassName, "address") ||
                        ContainsIgnoreCase(e.ClassName, "addr"));
                if (nameEl is not null)
                {
                    name = Clean(nameEl.TextContent);
                    address = addrEl is not null ? Clean(addrEl.TextContent) : null;
                }
            }

            // ── Strategy 3: <a>NAME</a> + sibling <p>/<span> in enclosing
            //    container (Popeyes style) — the <a> text IS the name, and
            //    the address is a sibling element inside the nearest
            //    li/article/div.restaurant wrapper.
            if (name is null)
            {
                var aText = Clean(a.TextContent);
                if (!string.IsNullOrEmpty(aText))
                {
                    var container = a.Closest("li, article, div.restaurant") ?? a.ParentElement;
                    var addrEl = container?.QuerySelectorAll("p, span")
                        .FirstOrDefault(e => !string.Equals(Clean(e.TextContent), aText, StringComparison.Ordinal));
                    name = aText;
                    address = addrEl is not null ? Clean(addrEl.TextContent) : null;
                }
            }

            // ── Strategy 4: leaf-text fallback — bare navigation link with no
            //    structured name/address (e.g. a district index link).
            //    Recorded with name = null so callers can distinguish "this is
            //    a navigation link to sub-crawl" from "this is a restaurant".
            if (name is null)
            {
                name = null; // explicit: a bare link is never treated as a named card.
            }

            // ── Final guard ──────────────────────────────────────────────────
            // Never store junk a strategy may have picked up. A lone "+" (an
            // expand toggle), a divider, or a bare number is not a real name —
            // null it so callers treat the link as navigation, not a restaurant.
            // A nameless card carries no address either.
            if (!TextNormalization.IsPlausibleName(name))
                name = null;
            if (name is null || !TextNormalization.IsPlausibleAddress(address))
                address = null;

            results.Add(new RestaurantCard(absHref, name, address));
        }

        return results;
    }

    private static bool ContainsIgnoreCase(string? haystack, string needle) =>
        !string.IsNullOrEmpty(haystack) &&
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var decoded = WebUtility.HtmlDecode(text);
        var collapsed = string.Join(' ', decoded.Split(
            (char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length == 0 ? null : collapsed;
    }
}
