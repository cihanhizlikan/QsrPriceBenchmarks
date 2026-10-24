using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace QsrPriceBenchmarks.Ui;

public partial class App : System.Windows.Application
{
    public App()
    {
        // A WPF GUI app prints nothing to the console and routes unhandled
        // exceptions to the Windows Event Log, so a startup crash looks like the
        // app silently failing to launch. Capture everything to a log file next
        // to the exe (falling back to %TEMP%) so the real cause is visible.
        AppDomain.CurrentDomain.UnhandledException +=
            (_, e) => LogCrash(e.ExceptionObject as Exception, "AppDomain.UnhandledException");
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    /// <summary>
    /// UI-thread exceptions are logged and shown to the user, but no longer
    /// kill the process (the original handler only logged, leaving e.Handled
    /// false — so e.g. selecting a database file locked by another instance
    /// took the whole app down). Marking the exception handled keeps the app
    /// alive; the message box tells the user what failed and where it's logged.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception, "DispatcherUnhandledException");
        e.Handled = true;
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}\n\n" +
            "The operation was cancelled; the application will keep running. " +
            "Details were written to qsr-startup-error.log.",
            "QSR Price Benchmarks", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    internal static void LogCrash(Exception? ex, string source)
    {
        foreach (string dir in new[] { AppContext.BaseDirectory, Path.GetTempPath() })
        {
            try
            {
                string path = Path.Combine(dir, "qsr-startup-error.log");
                StringBuilder report = new();
                report.AppendLine(CultureInfo.InvariantCulture, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({source})");
                report.AppendLine(ex?.ToString() ?? "(no exception object)");
                report.AppendLine(new string('-', 70));
                File.AppendAllText(path, report.ToString());
                return;
            }
            catch
            {
                // Couldn't write here — try the next directory; logging must never throw.
            }
        }
    }
}
