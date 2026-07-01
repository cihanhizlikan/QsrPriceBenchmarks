using System;
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
        DispatcherUnhandledException +=
            (_, e) => LogCrash(e.Exception, "DispatcherUnhandledException");
    }

    internal static void LogCrash(Exception? ex, string source)
    {
        foreach (string dir in new[] { AppContext.BaseDirectory, Path.GetTempPath() })
        {
            try
            {
                string path = Path.Combine(dir, "qsr-startup-error.log");
                StringBuilder report = new();
                report.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ({source})");
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
