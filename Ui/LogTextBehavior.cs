using System.Windows;
using System.Windows.Controls;

namespace QsrPriceBenchmarks.Ui;

/// <summary>
/// Attached property that fills a <see cref="TextBlock"/> with the coloured
/// <see cref="System.Windows.Documents.Run"/> elements produced by
/// <see cref="LogLineColouriser"/> for the bound log line. This keeps the
/// per-token colouring identical to the CLI without embedding ANSI codes.
/// </summary>
public static class LogTextBehavior
{
    public static readonly DependencyProperty LineProperty =
        DependencyProperty.RegisterAttached(
            "Line", typeof(string), typeof(LogTextBehavior),
            new PropertyMetadata(null, OnLineChanged));

    public static void SetLine(DependencyObject d, string value) => d.SetValue(LineProperty, value);
    public static string GetLine(DependencyObject d) => (string)d.GetValue(LineProperty);

    private static void OnLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (e.NewValue is string line)
            foreach (var run in LogLineColouriser.ToRuns(line))
                tb.Inlines.Add(run);
    }
}
