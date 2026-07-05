using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace QsrPriceBenchmarks.Ui.Converters;

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public Visibility WhenTrue  { get; set; } = Visibility.Visible;
    public Visibility WhenFalse { get; set; } = Visibility.Collapsed;
    public object Convert(object v, Type t, object p, CultureInfo c) =>
        v is true ? WhenTrue : WhenFalse;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) =>
        v is Visibility vis && vis == WhenTrue;
}

[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) =>
        v is bool b ? !b : v;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) =>
        v is bool b ? !b : v;
}

/// <summary>Returns Collapsed when value is null (any reference type), Visible otherwise.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public Visibility WhenNull    { get; set; } = Visibility.Collapsed;
    public Visibility WhenNotNull { get; set; } = Visibility.Visible;
    public object Convert(object v, Type t, object p, CultureInfo c) =>
        v is null ? WhenNull : WhenNotNull;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}

[ValueConversion(typeof(decimal), typeof(string))]
public sealed class PriceChangeColorConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) =>
        v is decimal d ? (d > 0 ? "#EF5350" : d < 0 ? "#66BB6A" : "#9E9E9E") : "#9E9E9E";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}
