using System.Net;
using AngleSharp.Dom;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Core.Util;

namespace QsrPriceBenchmarks.Core.Scraping;

/// <summary>
/// Extracts menu items from a Tıkla Gelsin restaurant page's LIVE DOM — what
/// the browser renders after a menu tab is clicked. The first tab is read from
/// the server-rendered payload instead (<see cref="NextJsTabParser"/>); every
/// other tab only exists once React renders it.
///
/// The shape, measured on live pages 2026-10-25:
///   - Only the active tab's products are in the DOM, in one
///     <c>div.grid.lg:grid-cols-2</c> per sub-category ("Et Sandviçler",
///     "Tavuk Sandviçler", …) — so a tab can have several grids, and all of
///     them are read.
///   - Each card is <c>id="product-card-N"</c>, its name the
///     <c>id="product-card-N-title"</c> element and its price a
///     <c>font-bold</c> <c>text-marker</c> (or <c>text-errorText</c>) span inside
///     <c>id="product-card-N-price"</c>. Before this, cards carried a single
///     shared <c>id="restaurant-card-title"</c>; the server-rendered payload
///     still does.
///   - The description (<c>product-card-N-description</c>) holds portion text
///     like "2 Adet …" and is never taken as the name.
/// </summary>
public static class TgMenuDomParser
{
    private const string TitleSuffix = "-title";

    public static List<MenuItem> Parse(IDocument document)
    {
        List<MenuItem> results = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        IEnumerable<IElement> grids = document.QuerySelectorAll("div.grid")
            .Where(g => (g.ClassName ?? "").Contains("lg:grid-cols-2", StringComparison.Ordinal));
        foreach (IElement grid in grids)
        {
            foreach (IElement title in grid.QuerySelectorAll("[id^='product-card-'][id$='-title']"))
            {
                string name = WebUtility.HtmlDecode(title.TextContent).Trim();
                if (string.IsNullOrEmpty(name) || !seen.Add(name))
                {
                    continue;
                }

                string cardId = title.Id![..^TitleSuffix.Length];
                decimal? price = PriceOf(document.GetElementById(cardId + "-price"));
                if (price is null)
                {
                    seen.Remove(name);
                    continue;
                }

                results.Add(new MenuItem(name, price.Value));
            }
        }

        return results;
    }

    private static decimal? PriceOf(IElement? priceBox)
    {
        IElement? priceEl = priceBox?.QuerySelectorAll("span, div")
            .FirstOrDefault(e => (e.ClassName ?? "").Contains("text-marker", StringComparison.Ordinal)
                || (e.ClassName ?? "").Contains("text-errorText", StringComparison.Ordinal));
        return priceEl is null ? null : PriceParsing.ParsePrice(priceEl.TextContent);
    }
}
