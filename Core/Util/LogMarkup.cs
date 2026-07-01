namespace QsrPriceBenchmarks.Core.Util;

/// <summary>
/// Colour-agnostic markers for log messages. Core wraps scraped text values
/// (e.g. province / district names) with <see cref="Value"/>; the CLI
/// (<see cref="ConsoleColouriser"/>) and WPF (LogLineColouriser) renderers each
/// recognise the marker pair, render the enclosed text in <b>dim cyan</b> — the
/// same treatment the Python prototype gave scraped values — and then strip the
/// markers. Keeping the markers colour-agnostic means Core never has to know
/// whether its output is going to an ANSI terminal or to WPF Runs.
///
/// The markers are Unicode Private-Use-Area code points, so they never collide
/// with real scraped text and stay invisible to anything that doesn't interpret
/// them.
/// </summary>
public static class LogMarkup
{
    public const char ValueStart = '\uE000';
    public const char ValueEnd   = '\uE001';

    /// <summary>Mark <paramref name="text"/> as a scraped value (rendered dim cyan).</summary>
    public static string Value(string text) => $"{ValueStart}{text}{ValueEnd}";

    /// <summary>Remove any value markers from <paramref name="s"/> (used when colour is off).</summary>
    public static string Strip(string s) =>
        s.IndexOf(ValueStart) < 0 && s.IndexOf(ValueEnd) < 0
            ? s
            : s.Replace(ValueStart.ToString(), "").Replace(ValueEnd.ToString(), "");
}
