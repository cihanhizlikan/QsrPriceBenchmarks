using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace QsrPriceBenchmarks.Core.Util;

/// <summary>
/// String-cleanup helpers ported from the Python prototype's
/// _title / _strip_boilerplate / slogan-detection logic.
/// </summary>
public static partial class TextNormalization
{
    // Turkish culture for proper İ/ı/i/I casing.
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[""'\u2018\u2019\u201C\u201D]")]
    private static partial Regex QuoteCharsRegex();

    /// <summary>
    /// Normalise and Turkish-title-case a string for storage in NAME/ADDRESS
    /// columns. Returns null for null/empty/whitespace-only input — never an
    /// empty string, so callers can use `is not null` to mean "has a value".
    /// </summary>
    public static string? Title(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;

        var t = s.Trim();
        t = QuoteCharsRegex().Replace(t, "");
        t = WhitespaceRegex().Replace(t, " ").Trim();

        if (t.Length == 0)
            return null;

        // Turkish title-case: lowercase the string under Turkish rules first
        // (so "İSTANBUL" -> "istanbul" correctly, not "iSTANBUL" as with
        // invariant ToLower), then apply TextInfo.ToTitleCase.
        var lowered = t.ToLower(TurkishCulture);
        var titled = TurkishCulture.TextInfo.ToTitleCase(lowered);
        return titled;
    }

    /// <summary>
    /// Strip common boilerplate suffixes chain websites append to restaurant
    /// names, e.g. "Telefon: 0212 ..." or "Adres: ...". Mirrors the Python
    /// _strip_boilerplate regexes — extend this list per-chain as new
    /// boilerplate patterns are discovered.
    /// </summary>
    public static string? StripBoilerplate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return raw;

        var t = raw;

        // Cut off anything from "Telefon" / "Tel:" / "Çalışma Saatleri" onward.
        var cutMarkers = new[] { "Telefon", "Tel:", "Çalışma Saatleri", "Harita", "Yol Tarifi" };
        foreach (var marker in cutMarkers)
        {
            var idx = t.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                t = t[..idx];
        }

        return WhitespaceRegex().Replace(t, " ").Trim();
    }

    /// <summary>
    /// True when <paramref name="text"/> looks like a marketing slogan rather
    /// than a real street address: no digits at all, and ends with "!".
    /// Real Turkish addresses always carry a building/floor/street number
    /// (e.g. "No: 106", "Kat 1"); slogans like
    /// "Usta Dönerci Lezzetleri Her An Yanında!" do not.
    /// </summary>
    public static bool LooksLikeSlogan(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return false;

        var trimmed = address.TrimEnd();
        var hasDigit = trimmed.Any(char.IsDigit);
        var endsWithBang = trimmed.EndsWith('!');
        return !hasDigit && endsWithBang;
    }

    /// <summary>
    /// True when <paramref name="address"/> contains at least one digit —
    /// the minimal signal that distinguishes a real street address from a
    /// navigation label / slogan / district name with no numeric component.
    /// </summary>
    public static bool IsRealAddress(string? address) =>
        !string.IsNullOrWhiteSpace(address) && address.Any(char.IsDigit);

    /// <summary>
    /// Strip a trailing address suffix from a combined "name + address"
    /// string, as chain websites sometimes concatenate the two with no
    /// separator. Mirrors update_location_details' suffix-strip step.
    /// </summary>
    public static string? StripAddressSuffix(string? name, string? address)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;
        if (string.IsNullOrWhiteSpace(address))
            return name;

        var n = name.Trim();
        var a = address.Trim();
        if (a.Length > 0 && n.EndsWith(a, StringComparison.Ordinal))
            return n[..^a.Length].Trim();

        return n;
    }

    /// <summary>True when <paramref name="slug"/> contains "test" (case-insensitive).</summary>
    public static bool IsTestSlug(string slug) =>
        slug.Contains("test", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="text"/> is a plausible restaurant NAME: at
    /// least two characters and containing at least one letter. Rejects junk a
    /// parser can pick up from expand/“+” toggles, dividers, or numeric badges
    /// (a lone "+", "-", or a bare number like "3"), which must never be stored
    /// as a name.
    /// </summary>
    public static bool IsPlausibleName(string? text) => HasLetterAndMinLength(text, 2);

    /// <summary>
    /// True when <paramref name="address"/> is a plausible street ADDRESS: at
    /// least two characters and containing at least one letter. Rejects numeric
    /// badges/counts (e.g. "1", "6") that sit next to the real address in some
    /// chains' card markup and were otherwise being stored as the address.
    /// </summary>
    public static bool IsPlausibleAddress(string? address) => HasLetterAndMinLength(address, 2);

    private static bool HasLetterAndMinLength(string? s, int min)
    {
        if (string.IsNullOrWhiteSpace(s))
            return false;
        var t = s.Trim();
        return t.Length >= min && t.Any(char.IsLetter);
    }
}
