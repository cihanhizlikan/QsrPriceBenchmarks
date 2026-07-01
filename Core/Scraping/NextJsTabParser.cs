using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using QsrPriceBenchmarks.Core.Models;

namespace QsrPriceBenchmarks.Core.Scraping;

/// <summary>
/// Extracts menu item (name, price) pairs from TG (tiklagelsin.com) restaurant
/// pages. TG is a Next.js app: the React component tree for the *first* tab
/// (server-rendered) is serialised as JSON inside
/// <c>self.__next_f.push([1, "..."])</c> script blocks. Cards are NOT present
/// as real DOM nodes in the static HTML — only in this JSON payload.
/// </summary>
public static partial class NextJsTabParser
{
    // Matches each self.__next_f.push([1, "...escaped json string..."]) call.
    // The captured group is itself a JSON-string-escaped payload (it was
    // written into the page as a JS string literal).
    [GeneratedRegex(@"self\.__next_f\.push\(\[1,\s*""((?:[^""\\]|\\.)*)""\]\)")]
    private static partial Regex NextFPushRegex();

    // Strips the Next.js streaming-chunk numeric prefix, e.g. "29:[...]" -> "[...]"
    [GeneratedRegex(@"^\d+:")]
    private static partial Regex ChunkPrefixRegex();

    /// <summary>
    /// Parse all menu items from the page's SSR JSON payload.
    /// </summary>
    /// <param name="html">Full page HTML (or just the relevant script tag text).</param>
    /// <param name="tabName">
    /// When non-null, only items whose enclosing "pb-2" section heading
    /// contains this tab name are returned. When null, every item found in
    /// the payload is returned (used for the first/SSR tab, where the
    /// section heading is not rendered in the static HTML).
    /// </param>
    public static List<MenuItem> Parse(string html, string? tabName = null)
    {
        var results = new List<MenuItem>();

        foreach (Match blockMatch in NextFPushRegex().Matches(html))
        {
            var rawBlock = blockMatch.Groups[1].Value;

            // Only the block(s) containing actual product cards are useful.
            if (!rawBlock.Contains("restaurant-card-title"))
                continue;

            string unescaped;
            try
            {
                // The captured text is a JSON string literal's *contents*
                // (i.e. what was between the quotes). Wrap it back in quotes
                // and let System.Text.Json unescape it properly — this
                // mirrors Python's json.loads(f'"{block}"').
                unescaped = JsonSerializer.Deserialize<string>("\"" + rawBlock + "\"")
                            ?? rawBlock;
            }
            catch (JsonException)
            {
                // Fall back to using the raw block as-is; worst case the
                // unicode escapes stay literal and parsing fails below.
                unescaped = rawBlock;
            }

            unescaped = ChunkPrefixRegex().Replace(unescaped, "", 1);

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(unescaped);
            }
            catch (JsonException)
            {
                continue;
            }

            using (doc)
            {
                var walker = new TreeWalker(tabName);
                walker.Walk(doc.RootElement);
                results.AddRange(walker.Results);
            }

            // Only one block contains the product data — stop after it.
            break;
        }

        return results;
    }

    /// <summary>
    /// Recursive walker over the Next.js-serialised component tree.
    /// Mirrors the Python closure-based _walk() function: tracks the most
    /// recently seen card title/description, and the most recently seen
    /// section heading (pb-2 div), then pairs a title with its price when a
    /// price-styled span is encountered.
    /// </summary>
    private sealed class TreeWalker(string? tabName)
    {
        private string? _currentSection;
        private string? _pendingTitle;

        public List<MenuItem> Results { get; } = new();

        public void Walk(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    WalkObject(element);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                        Walk(item);
                    break;
                default:
                    break;
            }
        }

        private void WalkObject(JsonElement obj)
        {
            var elId = GetStringProp(obj, "id") ?? "";
            var className = GetStringProp(obj, "className") ?? "";

            // Section heading (e.g. "Popüler Ürünler") — pb-2 container
            if (className.Contains("pb-2"))
            {
                var children = GetStringProp(obj, "children");
                if (!string.IsNullOrWhiteSpace(children))
                    _currentSection = children.Trim();
            }

            // Item title (caption)
            if (elId == "restaurant-card-title")
            {
                var title = GetStringProp(obj, "children");
                if (!string.IsNullOrWhiteSpace(title))
                    _pendingTitle = WebUtility.HtmlDecode(title.Trim());
            }

            // Price: font-bold + (text-errorText | text-marker)
            if (className.Contains("font-bold") &&
                (className.Contains("text-errorText") || className.Contains("text-marker")))
            {
                var priceStr = GetStringProp(obj, "children");
                if (priceStr is not null && _pendingTitle is not null)
                {
                    var price = Util.PriceParsing.ParsePrice(priceStr);
                    if (price is not null)
                    {
                        var inSection = tabName is null
                            || (_currentSection is not null
                                && _currentSection.Contains(tabName.Trim()));

                        if (inSection)
                            Results.Add(new MenuItem(_pendingTitle, price.Value));
                    }
                    _pendingTitle = null;
                }
            }

            // Recurse into every property value (children/props/etc).
            foreach (var prop in obj.EnumerateObject())
                Walk(prop.Value);
        }

        private static string? GetStringProp(JsonElement obj, string name)
        {
            if (obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }
    }
}
