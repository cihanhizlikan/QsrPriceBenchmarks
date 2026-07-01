namespace QsrPriceBenchmarks.Core.Models;

/// <summary>
/// Each chain's real-life brand colour as an <c>#RRGGBB</c> hex string. Single
/// source of truth shared by the UI sidebar badges and the Excel export header
/// row. Unknown chains fall back to <see cref="FallbackHex"/> (neutral grey).
/// NOTE: Sbarro's brand value arrived malformed ("#cca0d2a", 7 hex digits) and
/// is read here as #CA0D2A (red) to match the brand.
/// </summary>
public static class BrandPalette
{
    private static readonly Dictionary<string, string> Hex = new(StringComparer.Ordinal)
    {
        ["Arby's"]      = "#D91C22", // red
        ["Burger King"] = "#FAB111", // amber/yellow
        ["Popeyes"]     = "#FF7C00", // orange
        ["Sbarro"]      = "#CA0D2A", // red
        ["Subway"]      = "#005543", // dark green
        ["Usta Dönerci"] = "#D93832", // red
        ["Usta Pideci"] = "#E12325", // red
    };

    /// <summary>Neutral header/badge colour used when a chain has no brand entry.</summary>
    public const string FallbackHex = "#2F2F2F";

    /// <summary>Return the chain's brand hex, or <see cref="FallbackHex"/> if unknown.</summary>
    public static string HexFor(string chainName) =>
        Hex.TryGetValue(chainName, out string? hex) ? hex : FallbackHex;

    /// <summary>True when the chain has an explicit brand colour (not the fallback).</summary>
    public static bool HasBrand(string chainName) => Hex.ContainsKey(chainName);
}
