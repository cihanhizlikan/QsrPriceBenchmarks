using System.Globalization;
using System.Text.RegularExpressions;

namespace QsrPriceBenchmarks.Core.Util;

public static partial class PriceParsing
{
    // Matches the numeric portion of a Turkish-formatted price string, e.g.
    // "340,00 TL" / "1.250,50 TL" / "340 TL". Turkish uses '.' as the
    // thousands separator and ',' as the decimal separator.
    [GeneratedRegex(@"(\d{1,3}(?:\.\d{3})*|\d+)(?:,(\d+))?")]
    private static partial Regex PriceRegex();

    /// <summary>
    /// Parse a Turkish-formatted price string such as "340,00 TL" into a
    /// decimal. Returns null when no numeric content can be found.
    /// </summary>
    public static decimal? ParsePrice(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var m = PriceRegex().Match(raw);
        if (!m.Success)
            return null;

        var integerPart = m.Groups[1].Value.Replace(".", "");
        var fractionPart = m.Groups[2].Success ? m.Groups[2].Value : "0";

        var normalised = $"{integerPart}.{fractionPart}";
        return decimal.TryParse(
            normalised,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }
}
