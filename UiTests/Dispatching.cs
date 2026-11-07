using System.Diagnostics;
using System.Windows.Threading;

namespace QsrPriceBenchmarks.UiTests;

/// <summary>
/// Runs the WPF dispatcher on the test's thread so bindings, async continuations and layout happen while the test
/// waits — the one copy of the loop the UI tests used to write out each (plan WP-T4).
/// </summary>
internal static class Dispatching
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan TurnLength = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Pumps until <paramref name="done"/> holds; after 20 s it fails with
    /// <paramref name="failure"/> ("The tab list did not reach the expected state"), so a hang names its cause.
    /// </summary>
    public static void PumpUntil(Func<bool> done, string failure)
    {
        long started = Stopwatch.GetTimestamp();
        while (!done())
        {
            if (Stopwatch.GetElapsedTime(started) > Bound)
            {
                throw new TimeoutException($"{failure} within {Bound.TotalSeconds:0} s.");
            }
            Turn(TurnLength);
        }
    }

    /// <summary>
    /// Pumps for a fixed time — for work with no state to wait on, such as a binding re-evaluating. Not a wait for
    /// something: it never fails.
    /// </summary>
    public static void PumpFor(TimeSpan settle)
    {
        long started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < settle)
        {
            Turn(TurnLength);
        }
    }

    /// <summary>Runs the dispatcher for one turn of <paramref name="length"/>, processing everything queued.</summary>
    public static void Turn(TimeSpan length)
    {
        DispatcherFrame frame = new();
        DispatcherTimer tick = new(length, DispatcherPriority.Background,
            (_, _) => { frame.Continue = false; }, Dispatcher.CurrentDispatcher);
        tick.Start();
        Dispatcher.PushFrame(frame);
        tick.Stop();
    }
}
