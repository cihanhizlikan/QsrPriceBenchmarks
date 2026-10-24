using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace QsrPriceBenchmarks.Ui.Converters;

[ValueConversion(typeof(bool), typeof(Visibility))]
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public Visibility WhenTrue { get; set; } = Visibility.Visible;
    public Visibility WhenFalse { get; set; } = Visibility.Collapsed;
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        return v is true ? WhenTrue : WhenFalse;
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c)
    {
        return v is Visibility vis && vis == WhenTrue;
    }
}

[ValueConversion(typeof(bool), typeof(bool))]
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        return v is bool b ? !b : v;
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c)
    {
        return v is bool b ? !b : v;
    }
}

/// <summary>Returns Collapsed when value is null (any reference type), Visible otherwise.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public Visibility WhenNull { get; set; } = Visibility.Collapsed;
    public Visibility WhenNotNull { get; set; } = Visibility.Visible;
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        return v is null ? WhenNull : WhenNotNull;
    }

    public object ConvertBack(object v, Type t, object p, CultureInfo c)
    {
        throw new NotSupportedException();
    }
}
