using System.Windows;

namespace QsrPriceBenchmarks.Ui;

/// <summary>
/// Carries the window's DataContext to elements outside its inheritance tree — a DataGrid column's header is
/// one: a header element finds no DataGrid ancestor when its bindings resolve. As a <see cref="Freezable"/> in
/// the window's resources it inherits the DataContext itself, and <see cref="Data"/> hands it on.
/// </summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get { return GetValue(DataProperty); }
        set { SetValue(DataProperty, value); }
    }

    protected override Freezable CreateInstanceCore()
    {
        return new BindingProxy();
    }
}
