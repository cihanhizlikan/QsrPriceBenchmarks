using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using QsrPriceBenchmarks.Core.Util;

namespace QsrPriceBenchmarks.Ui;

/// <summary>
/// WPF equivalent of the CLI's <c>ConsoleColouriser</c>: turns a single log
/// line into a sequence of coloured <see cref="Run"/> elements using the same
/// three-level rule as the Python prototype and the CLI, in theme brushes:
///
///   numbers / symbols (✓ ✗ → ↳)  →  BrushLightCyan
///   scraped values / URLs         →  BrushDimCyan
///   all other text                →  BrushText
///   errors (lines starting ✗)     →  BrushPurple
///   info / warning (⚠ ℹ) / seps   →  BrushDimCyan
///   [Step N] / SKIP / FORCE       →  BrushBg3 background, number BrushLightCyan
///
/// Returns Runs (not a styled string) so the colours render natively in a WPF
/// TextBlock without embedding ANSI codes.
/// </summary>
public static class LogLineColouriser
{
    // The colours come from Ui/Themes/Dark.xaml, the one place the app defines a
    // colour (plan WP-U1); the theme's comments record which ANSI code each one
    // stands for. Numbers/symbols render in bright cyan; scraped values
    // (province/district names), URLs and secondary lines in a genuinely dimmer
    // cyan, so the two read differently.
    private static readonly Brush LightCyan = ThemeBrush("BrushLightCyan");
    private static readonly Brush DimCyan = ThemeBrush("BrushDimCyan");
    private static readonly Brush White = ThemeBrush("BrushText");
    private static readonly Brush Purple = ThemeBrush("BrushPurple");
    private static readonly Brush StepBg = ThemeBrush("BrushBg3");

    /// <summary>
    /// Explicit, so the brushes are looked up on first use — after the
    /// application has merged its theme — and never earlier.
    /// </summary>
    static LogLineColouriser()
    {
    }

    private static readonly Regex ReSkipForce = new(@"\b(SKIP|FORCE)\b", RegexOptions.Compiled);
    private static readonly Regex ReUrl = new(@"https?://\S+", RegexOptions.Compiled);
    private static readonly Regex ReSymbolsNum = new(@"[✓✗→↳]|(?<![\w/._-])\d[\d,.]*(?![\w/._-])", RegexOptions.Compiled);
    private static readonly Regex ReStepTag = new(@"\[Step\s+(\w+)\]", RegexOptions.Compiled);
    private static readonly Regex ReRatio = new(@"\[(\d+)/(\d+)\]", RegexOptions.Compiled);
    private static readonly Regex ReSeparator = new(@"^[-=]{3,}", RegexOptions.Compiled);
    private static readonly Regex ReValue = new($"{LogMarkup.ValueStart}(.*?){LogMarkup.ValueEnd}", RegexOptions.Compiled);

    /// <summary>
    /// A brush from the merged theme. A missing key throws rather than falling
    /// back to a colour of its own, which would quietly re-create the second
    /// palette this replaced.
    /// </summary>
    private static Brush ThemeBrush(string key)
    {
        return Application.Current?.TryFindResource(key) as Brush
            ?? throw new InvalidOperationException($"The theme (Ui/Themes/Dark.xaml) defines no brush '{key}'.");
    }

    /// <summary>Convert one log line into coloured Runs.</summary>
    public static IEnumerable<Run> ToRuns(string msg)
    {
        string stripped = msg.TrimStart();

        // Whole-line categories first (match Python precedence).
        if (stripped.Length > 0 &&
            (stripped[0] is '\u2500' or '\u2550' or '\u2501' || ReSeparator.IsMatch(stripped)))
        {
            return [new Run(LogMarkup.Strip(msg)) { Foreground = DimCyan }];
        }

        if (stripped.StartsWith('✗'))
        {
            return [new Run(LogMarkup.Strip(msg)) { Foreground = Purple }];
        }

        if (stripped.StartsWith('⚠') || stripped.StartsWith('\u2139'))
        {
            return [new Run(LogMarkup.Strip(msg)) { Foreground = DimCyan }];
        }

        // Widget pass: [Step N] and [n/m] both render as dark-bg widgets with
        // light-cyan numbers and white brackets/slash. Find every widget match
        // in order, colour the gaps between them as normal segments.
        List<Run> runs = [];
        int cursor = 0;

        // Collect all widget matches (step tags + ratios), ordered by position.
        List<(int Start, int Len, Run[] Parts)> widgets = [];
        foreach (Match m in ReStepTag.Matches(msg))
        {
            widgets.Add((m.Index, m.Length, StepRuns(m.Groups[1].Value)));
        }
        foreach (Match m in ReRatio.Matches(msg))
        {
            widgets.Add((m.Index, m.Length, RatioRuns(m.Groups[1].Value, m.Groups[2].Value)));
        }
        widgets.Sort((a, b) => a.Start.CompareTo(b.Start));

        foreach ((int start, int len, Run[] parts) in widgets)
        {
            if (start < cursor)
            {
                continue; // skip overlaps (shouldn't happen)
            }
            if (start > cursor)
            {
                runs.AddRange(ColourSegment(msg[cursor..start]));
            }
            runs.AddRange(parts);
            cursor = start + len;
        }
        if (cursor < msg.Length)
        {
            runs.AddRange(ColourSegment(msg[cursor..]));
        }

        return runs;
    }

    /// <summary>Runs for a [Step N] widget: white brackets, light-cyan number, dark bg.</summary>
    private static Run[] StepRuns(string num)
    {
        return
    [
        new Run("[Step ") { Foreground = White,     Background = StepBg },
        new Run(num)      { Foreground = LightCyan, Background = StepBg },
        new Run("]")      { Foreground = White,     Background = StepBg },
    ];
    }

    /// <summary>Runs for a [n/m] ratio widget: white brackets/slash, light-cyan numbers, dark bg.</summary>
    private static Run[] RatioRuns(string cur, string tot)
    {
        return
    [
        new Run("[")  { Foreground = White,     Background = StepBg },
        new Run(cur)  { Foreground = LightCyan, Background = StepBg },
        new Run("/")  { Foreground = White,     Background = StepBg },
        new Run(tot)  { Foreground = LightCyan, Background = StepBg },
        new Run("]")  { Foreground = White,     Background = StepBg },
    ];
    }

    private static IEnumerable<Run> ColourSegment(string seg)
    {
        if (string.IsNullOrEmpty(seg))
        {
            yield break;
        }

        // Protected spans: marked values (dim cyan), SKIP/FORCE (dark bg), URLs (dim cyan).
        List<(int Start, int End, Run Run)> spans = [];
        foreach (Match m in ReValue.Matches(seg))
        {
            spans.Add((m.Index, m.Index + m.Length,
                new Run(m.Groups[1].Value) { Foreground = DimCyan }));
        }
        foreach (Match m in ReSkipForce.Matches(seg))
        {
            spans.Add((m.Index, m.Index + m.Length,
                new Run($" {m.Value} ") { Foreground = White, Background = StepBg }));
        }
        foreach (Match m in ReUrl.Matches(seg))
        {
            spans.Add((m.Index, m.Index + m.Length,
                new Run(m.Value) { Foreground = DimCyan }));
        }
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));

        // Remove overlaps (first-wins, matches Python).
        List<(int Start, int End, Run Run)> clean = [];
        int end = 0;
        foreach ((int Start, int End, Run Run) s in spans)
        {
            if (s.Start >= end) { clean.Add(s); end = s.End; }
        }

        int pos = 0;
        foreach ((int s, int e, Run run) in clean)
        {
            if (s > pos)
            {
                foreach (Run r in ColourText(seg[pos..s]))
                {
                    yield return r;
                }
            }
            yield return run;
            pos = e;
        }
        if (pos < seg.Length)
        {
            foreach (Run r in ColourText(seg[pos..]))
            {
                yield return r;
            }
        }
    }

    private static IEnumerable<Run> ColourText(string t)
    {
        if (string.IsNullOrEmpty(t))
        {
            yield break;
        }

        int last = 0;
        foreach (Match m in ReSymbolsNum.Matches(t))
        {
            if (m.Index > last)
            {
                yield return new Run(t[last..m.Index]) { Foreground = White };
            }
            yield return new Run(m.Value) { Foreground = LightCyan };
            last = m.Index + m.Length;
        }
        if (last < t.Length)
        {
            yield return new Run(t[last..]) { Foreground = White };
        }
    }
}
