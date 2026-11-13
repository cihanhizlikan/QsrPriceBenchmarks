using System.ComponentModel;
using System.Windows;

namespace QsrPriceBenchmarks.Ui;

/// <summary>
/// Whether the window may animate, read from Windows' <i>Show animations</i> setting
/// (<see cref="SystemParameters.ClientAreaAnimation"/>) when the app starts. XAML binds it as
/// <c>{Binding Path=(local:Motion.Enabled)}</c>. When it is off, each cue holds its end state rather than
/// disappearing: the progress fill stays at full opacity, the header keeps its logo, and a marker keeps its dot
/// without the ring (WP-U8, 2026-11-13).
/// </summary>
public static class Motion
{
    private static bool _enabled = SystemParameters.ClientAreaAnimation;

    /// <summary>Raised when <see cref="Enabled"/> changes; WPF's binding engine listens for it by name.</summary>
    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

    /// <summary>True when the window may animate. Set only by the tests, which cannot change the Windows setting.</summary>
    public static bool Enabled
    {
        get { return _enabled; }
        set
        {
            if (_enabled == value)
            {
                return;
            }
            _enabled = value;
            StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(Enabled)));
        }
    }
}
