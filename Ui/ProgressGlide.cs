using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace QsrPriceBenchmarks.Ui;

/// <summary>
/// A progress bar's value, glided to rather than jumped to: bind <c>local:ProgressGlide.Value</c> in place of
/// <c>Value</c>, and the fill's width moves to each new value over 0.3 s. Yalbuz's progress fill does the same
/// (a CSS width transition), and keeps it when animations are off because the width is the bar's meaning
/// (WP-U8, 2026-11-13).
/// </summary>
public static class ProgressGlide
{
    private static readonly Duration Glide = new(TimeSpan.FromMilliseconds(300));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(double), typeof(ProgressGlide), new PropertyMetadata(0d, OnValueChanged));

    public static double GetValue(RangeBase bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        return (double)bar.GetValue(ValueProperty);
    }

    public static void SetValue(RangeBase bar, double value)
    {
        ArgumentNullException.ThrowIfNull(bar);
        bar.SetValue(ValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject target, DependencyPropertyChangedEventArgs change)
    {
        if (target is not RangeBase bar)
        {
            return;
        }
        DoubleAnimation glide = new((double)change.NewValue, Glide)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        bar.BeginAnimation(RangeBase.ValueProperty, glide, HandoffBehavior.SnapshotAndReplace);
    }
}
