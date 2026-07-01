using System.Text;
using System.Text.RegularExpressions;

namespace QsrPriceBenchmarks.Core.Util;

/// <summary>
/// Faithful C# port of the Python prototype's <c>_colourise()</c> pipeline.
/// Produces ANSI-escaped strings using the exact same three-level rule:
///
///   numbers / symbols (✓ ✗ → ↳)  →  light cyan   \e[96m
///   scraped text values / URLs    →  dim cyan     \e[2;96m
///   all other text                →  white        \e[97m
///   errors (lines starting ✗)     →  purple       \e[95m
///   dark-gray bg widgets          →  \e[48;5;234m  ([Step N], ratios, stats, SKIP/FORCE)
///
/// The CLI applies this to every log line. The colour codes and precedence
/// match the Python source byte-for-byte so output is visually identical.
/// </summary>
public static class ConsoleColouriser
{
    // ── Raw ANSI codes (identical to Python) ──────────────────────────────────
    private const string Esc   = "\u001b";
    private const string Reset = "\u001b[0m";
    private const string Bg    = "48;5;234";   // very dark gray background
    private const string W     = "97";         // white foreground
    private const string Lc    = "96";         // light cyan foreground
    private const string DimLc = "2;96";       // dim cyan foreground
    private const string Pur   = "95";         // purple foreground

    public static bool Enabled { get; set; } = true;

    // ── Low-level wrappers ─────────────────────────────────────────────────────
    private static string Wrap(string code, string text) =>
        Enabled ? $"{Esc}[{code}m{text}{Reset}" : text;

    public static string LightCyan(string t) => Wrap(Lc,    t);
    public static string DimCyan(string t)   => Wrap(DimLc, t);
    public static string White(string t)     => Wrap(W,     t);
    public static string Purple(string t)    => Wrap(Pur,   t);
    public static string Bold(string t)      => Wrap("1",   t);

    /// <summary>Dark-gray bg, white text — for SKIP/FORCE labels.</summary>
    private static string DGrayBg(string t)  => Wrap($"{Bg};{W}", t);

    // ── Pre-compiled regexes (mirror Python's _RE_* constants) ────────────────
    private static readonly Regex ReSkipForce  = new(@"\b(SKIP|FORCE)\b", RegexOptions.Compiled);
    private static readonly Regex ReUrl         = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex ReSymbolsNum  = new(@"[✓✗→↳]|(?<![\w/._-])\d[\d,.]*(?![\w/._-])", RegexOptions.Compiled);
    private static readonly Regex ReStepTag     = new(@"\[Step\s+(\w+)\]", RegexOptions.Compiled);
    private static readonly Regex ReRatio       = new(@"\[(\d+)/(\d+)\]", RegexOptions.Compiled);
    private static readonly Regex ReSeparator   = new(@"^[-=]{3,}", RegexOptions.Compiled);
    private static readonly Regex ReTagSuffix   = new(@"^(.*?)(\d+\w*)$", RegexOptions.Compiled);
    private static readonly Regex ReValue       = new($"{LogMarkup.ValueStart}(.*?){LogMarkup.ValueEnd}", RegexOptions.Compiled);

    // ── [n/m] ratio widget: numbers light cyan, brackets+slash white, dark bg ─
    private static string Ratio(string cur, string tot)
    {
        if (!Enabled) return $"[{cur}/{tot}]";
        var inner = $"{Esc}[{W}m[{Esc}[{Lc}m{cur}{Esc}[{W}m/{Esc}[{Lc}m{tot}{Esc}[{W}m]";
        return $"{Esc}[{Bg}m{inner}{Reset}";
    }

    // ── [Step N] widget: 'Step ' white, number light cyan, dark-gray bg ───────
    private static string Step(string tag)
    {
        if (!Enabled) return $"[{tag}]";
        var m = ReTagSuffix.Match(tag);
        string inner = m.Success
            ? $"{Esc}[{W}m[{m.Groups[1].Value}{Esc}[{Lc}m{m.Groups[2].Value}{Esc}[{W}m]"
            : $"{Esc}[{W}m[{tag}]";
        return $"{Esc}[{Bg}m{inner}{Reset}";
    }

    // ── Colour a plain (escape-free) text segment ─────────────────────────────
    private static string ColourSegment(string seg)
    {
        if (string.IsNullOrEmpty(seg)) return seg;

        // Protected spans in document order, overlaps removed
        // (marked values + URL + SKIP/FORCE).
        var spans = new List<(int Start, int End, string Coloured)>();
        foreach (Match m in ReValue.Matches(seg))
            spans.Add((m.Index, m.Index + m.Length, DimCyan(m.Groups[1].Value)));
        foreach (Match m in ReSkipForce.Matches(seg))
            spans.Add((m.Index, m.Index + m.Length, DGrayBg($" {m.Value} ")));
        foreach (Match m in ReUrl.Matches(seg))
            spans.Add((m.Index, m.Index + m.Length, DimCyan(m.Value)));
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        var clean = new List<(int Start, int End, string Coloured)>();
        int end = 0;
        foreach (var (s, e, col) in spans)
            if (s >= end) { clean.Add((s, e, col)); end = e; }

        var sb = new StringBuilder();
        int pos = 0;
        foreach (var (s, e, col) in clean)
        {
            if (s > pos) sb.Append(ColourText(seg[pos..s]));
            sb.Append(col);
            pos = e;
        }
        if (pos < seg.Length) sb.Append(ColourText(seg[pos..]));
        return sb.ToString();
    }

    // ── Colour numbers/symbols light cyan, everything else white ──────────────
    private static string ColourText(string t)
    {
        if (string.IsNullOrEmpty(t)) return t;
        var sb = new StringBuilder();
        int last = 0;
        foreach (Match m in ReSymbolsNum.Matches(t))
        {
            if (m.Index > last) sb.Append(White(t[last..m.Index]));
            sb.Append(LightCyan(m.Value));
            last = m.Index + m.Length;
        }
        if (last < t.Length) sb.Append(White(t[last..]));
        return sb.ToString();
    }

    /// <summary>
    /// Apply the three-level colour rules to one log message — the public
    /// entry point matching Python's <c>_colourise()</c>.
    /// </summary>
    public static string Colourise(string msg)
    {
        if (!Enabled) return LogMarkup.Strip(msg);

        var stripped = msg.TrimStart();

        // Already coloured (pre-built widget) — pass through.
        if (msg.Contains(Esc)) return msg;

        // Separator lines (box-drawing or --- / ===) → dim cyan.
        if (stripped.Length > 0 &&
            (stripped[0] is '\u2500' or '\u2550' or '\u2501' || ReSeparator.IsMatch(stripped)))
            return DimCyan(LogMarkup.Strip(msg));

        // Error lines → purple.
        if (stripped.StartsWith('✗')) return Purple(LogMarkup.Strip(msg));

        // Info / warning lines → dim cyan.
        if (stripped.StartsWith('⚠') || stripped.StartsWith('\u2139')) return DimCyan(LogMarkup.Strip(msg));

        // [Step N] → dark-gray bg widget, then colour the remaining segments.
        msg = ReStepTag.Replace(msg, m => Step($"Step {m.Groups[1].Value}"));

        // [n/m] ratio → dark-gray bg widget (matches Python _ratio()).
        msg = ReRatio.Replace(msg, m => Ratio(m.Groups[1].Value, m.Groups[2].Value));

        if (msg.Contains(Esc))
        {
            // Split on reset, colour only the escape-free parts.
            var parts = msg.Split(Reset);
            return string.Join(Reset,
                parts.Select(p => p.Contains(Esc) ? p : ColourSegment(p)));
        }

        return ColourSegment(msg);
    }

    // ── Banner — alternating light/dim cyan lines, all bold (matches Python) ──
    public static IReadOnlyList<string> BannerLines()
    {
        string[] art =
        {
            "   ██████  ███████ ██████  ",
            "   ██  ██ ██      ██   ██ ",
            "   ██  ██ ███████ ██████  ",
            "   ██  ██      ██ ██   ██ ",
            "   ██████  ███████ ██   ██ ",
        };
        var lines = new List<string>();
        for (int i = 0; i < art.Length; i++)
            lines.Add(Bold(i % 2 == 0 ? LightCyan(art[i]) : DimCyan(art[i])));
        return lines;
    }

    public static string BannerTitle()    => Bold(LightCyan("  QSR Price Benchmarks"));
    public static string Separator(int n) => DimCyan("  " + new string('─', n));
    public static string DbLine(string path) => DimCyan($"  DB: {path}");
}
