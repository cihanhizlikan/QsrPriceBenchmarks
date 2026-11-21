using System.Windows;
using QsrPriceBenchmarks.Ui;

namespace QsrPriceBenchmarks.UiTests;

/// <summary>
/// The main window as the tests that SHOW it build it: at the screen's corner, off the taskbar, and without taking
/// the focus from whatever the person at the machine is doing. A test that never shows its window builds a plain
/// <c>new MainWindow()</c>.
/// </summary>
internal static class ShownWindow
{
    public static MainWindow Create()
    {
        return new MainWindow
        {
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
        };
    }
}
