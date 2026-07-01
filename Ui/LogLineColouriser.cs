using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media;
using QsrPriceBenchmarks.Core.Util;

namespace QsrPriceBenchmarks.Ui;

/// <summary>
/// WPF equivalent of the Core <c>ConsoleColouriser</c>: turns a single log
/// line into a sequence of coloured <see cref="Run"/> elements using the exact
/// same three-level rule as the Python prototype and the CLI:
///
///   numbers / symbols (✓ ✗ → ↳)  →  light cyan   #00FFFF
///   scraped values / URLs         →  dim cyan     #00B3B3
///   all other text                →  white        #FFFFFF
///   errors (lines starting ✗)     →  purple       #FF55FF
///   info / warning (⚠ ℹ) / seps   →  dim cyan
///   [Step N] / SKIP / FORCE       →  dark-gray bg #1C1C1C, number light cyan
///
/// Returns Runs (not a styled string) so the colours render natively in a WPF
/// TextBlock without embedding ANSI codes.
/// </summary>
public static class LogLineColouriser
{
    // Numbers/symbols render in bright cyan; scraped values (province/district
    // names), URLs and secondary lines render in a genuinely dimmer cyan, so the
    // two read differently — matching the muted cyan of the inactive buttons.
    private static readonly Brush LightCyan = Freeze("#00FFFF");   // ANSI 96   bright cyan
    private static readonly Brush DimCyan   = Freeze("#00B3B3");   // ANSI 2;96 dimmed cyan
    private static readonly Brush White     = Freeze("#FFFFFF");   // ANSI 97
    private static readonly Brush Purple    = Freeze("#FF00FF");   // ANSI 95
    private static readonly Brush StepBg     = Freeze("#1C1C1C");  // ANSI 48;5;234

    private static readonly Regex ReSkipForce  = new(@"\b(SKIP|FORCE)\b", RegexOptions.Compiled);
    private static readonly Regex ReUrl         = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex ReSymbolsNum  = new(@"[✓✗→↳]|(?<![\w/._-])\d[\d,.]*(?![\w/._-])", RegexOptions.Compiled);
    private static readonly Regex ReStepTag     = new(@"\[Step\s+(\w+)\]", RegexOptions.Compiled);
    private static readonly Regex ReRatio       = new(@"\[(\d+)/(\d+)\]", RegexOptions.Compiled);
    private static readonly Regex ReSeparator   = new(@"^[-=]{3,}", RegexOptions.Compiled);
    private static readonly Regex ReValue       = new($"{LogMarkup.ValueStart}(.*?){LogMarkup.ValueEnd}", RegexOptions.Compiled);

    private static SolidColorBrush Freeze(string hex)
    {
        var b = (SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
        b.Freeze();
        return b;
    }

    /// <summary>Convert one log line into coloured Runs.</summary>
    public static IEnumerable<Run> ToRuns(string msg)
    {
        var stripped = msg.TrimStart();

        // Whole-line categories first (match Python precedence).
        if (stripped.Length > 0 &&
            (stripped[0] is '\u2500' or '\u2550' or '\u2501' || ReSeparator.IsMatch(stripped)))
            return new[] { new Run(LogMarkup.Strip(msg)) { Foreground = DimCyan } };

        if (stripped.StartsWith('✗'))
            return new[] { new Run(LogMarkup.Strip(msg)) { Foreground = Purple } };

        if (stripped.StartsWith('⚠') || stripped.StartsWith('\u2139'))
            return new[] { new Run(LogMarkup.Strip(msg)) { Foreground = DimCyan } };

        // Widget pass: [Step N] and [n/m] both render as dark-bg widgets with
        // light-cyan numbers and white brackets/slash. Find every widget match
        // in order, colour the gaps between them as normal segments.
        var runs = new List<Run>();
        int cursor = 0;

        // Collect all widget matches (step tags + ratios), ordered by position.
        var widgets = new List<(int Start, int Len, Run[] Parts)>();
        foreach (Match m in ReStepTag.Matches(msg))
            widgets.Add((m.Index, m.Length, StepRuns(m.Groups[1].Value)));
        foreach (Match m in ReRatio.Matches(msg))
            widgets.Add((m.Index, m.Length, RatioRuns(m.Groups[1].Value, m.Groups[2].Value)));
        widgets.Sort((a, b) => a.Start.CompareTo(b.Start));

        foreach (var (start, len, parts) in widgets)
        {
            if (start < cursor) continue; // skip overlaps (shouldn't happen)
            if (start > cursor)
                runs.AddRange(ColourSegment(msg[cursor..start]));
            runs.AddRange(parts);
            cursor = start + len;
        }
        if (cursor < msg.Length)
            runs.AddRange(ColourSegment(msg[cursor..]));

        return runs;
    }

    /// <summary>Runs for a [Step N] widget: white brackets, light-cyan number, dark bg.</summary>
    private static Run[] StepRuns(string num) => new[]
    {
        new Run("[Step ") { Foreground = White,     Background = StepBg },
        new Run(num)      { Foreground = LightCyan, Background = StepBg },
        new Run("]")      { Foreground = White,     Background = StepBg },
    };

    /// <summary>Runs for a [n/m] ratio widget: white brackets/slash, light-cyan numbers, dark bg.</summary>
    private static Run[] RatioRuns(string cur, string tot) => new[]
    {
        new Run("[")  { Foreground = White,     Background = StepBg },
        new Run(cur)  { Foreground = LightCyan, Background = StepBg },
        new Run("/")  { Foreground = White,     Background = StepBg },
        new Run(tot)  { Foreground = LightCyan, Background = StepBg },
        new Run("]")  { Foreground = White,     Background = StepBg },
    };

    private static IEnumerable<Run> ColourSegment(string seg)
    {
        if (string.IsNullOrEmpty(seg)) yield break;

        // Protected spans: marked values (dim cyan), SKIP/FORCE (dark bg), URLs (dim cyan).
        var spans = new List<(int Start, int End, Run Run)>();
        foreach (Match m in ReValue.Matches(seg))
            spans.Add((m.Index, m.Index + m.Length,
                new Run(m.Groups[1].Value) { Foreground = DimCyan }));
        foreach (Match m in ReSkipForce.Matches(seg))
            spans.Add((m.Index, m.Index + m.Length,
                new Run($" {m.Value} ") { Foreground = White, Background = StepBg }));
        foreach (Match m in ReUrl.Matches(seg))
            spans.Add((m.Index, m.Index + m.Length,
                new Run(m.Value) { Foreground = DimCyan }));
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Remove overlaps (first-wins, matches Python).
        var clean = new List<(int Start, int End, Run Run)>();
        int end = 0;
        foreach (var s in spans)
            if (s.Start >= end) { clean.Add(s); end = s.End; }

        int pos = 0;
        foreach (var (s, e, run) in clean)
        {
            if (s > pos)
                foreach (var r in ColourText(seg[pos..s])) yield return r;
            yield return run;
            pos = e;
        }
        if (pos < seg.Length)
            foreach (var r in ColourText(seg[pos..])) yield return r;
    }

    private static IEnumerable<Run> ColourText(string t)
    {
        if (string.IsNullOrEmpty(t)) yield break;
        int last = 0;
        foreach (Match m in ReSymbolsNum.Matches(t))
        {
            if (m.Index > last)
                yield return new Run(t[last..m.Index]) { Foreground = White };
            yield return new Run(m.Value) { Foreground = LightCyan };
            last = m.Index + m.Length;
        }
        if (last < t.Length)
            yield return new Run(t[last..]) { Foreground = White };
    }
}
