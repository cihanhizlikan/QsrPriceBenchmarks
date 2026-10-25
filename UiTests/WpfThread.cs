using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;

namespace QsrPriceBenchmarks.UiTests;

/// <summary>
/// One STA thread with a running dispatcher and one <see cref="Application"/> on
/// it, shared by every test that needs a real shown window.
///
/// <para>
/// WPF allows exactly one <c>Application</c> per AppDomain, owned by the thread
/// that made it, so a second test creating its own throws. The app's theme is
/// merged into it here, or every <c>{StaticResource}</c> in the window would fail
/// to resolve. It is a plain <c>Application</c>, not the app's <c>App</c>: that
/// one installs crash handlers that show message boxes.
/// </para>
/// </summary>
public static class WpfThread
{
    private static readonly Lazy<Dispatcher> Ui = new(Create, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>How long one body may run before the test fails instead of hanging the suite.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    /// <summary>Runs <paramref name="body"/> on the UI thread and rethrows whatever it threw, with its stack.</summary>
    public static void Run(Action body)
    {
        ExceptionDispatchInfo? failure = null;
        DispatcherOperation operation = Ui.Value.InvokeAsync(() =>
        {
            try
            {
                body();
            }
            catch (Exception error)
            {
                failure = ExceptionDispatchInfo.Capture(error);
            }
        });
        if (operation.Wait(Bound) != DispatcherOperationStatus.Completed)
        {
            throw new TimeoutException($"The UI-thread body did not finish within {Bound.TotalSeconds}s.");
        }
        failure?.Throw();
    }

    private static Dispatcher Create()
    {
        SQLitePCL.Batteries_V2.Init();

        Dispatcher? dispatcher = null;
        using ManualResetEventSlim ready = new();
        Thread thread = new(() =>
        {
            // OnLastWindowClose is the default, so closing one test's window would
            // shut the application down under the next.
            Application application = new() { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/qsr-ui;component/Themes/Dark.xaml"),
            });
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(30));
        return dispatcher ?? throw new InvalidOperationException("The WPF thread did not come up.");
    }
}
