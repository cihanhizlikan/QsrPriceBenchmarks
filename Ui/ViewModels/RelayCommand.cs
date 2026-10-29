using System.Windows.Input;

namespace QsrPriceBenchmarks.Ui.ViewModels;

/// <summary>
/// Minimal <see cref="ICommand"/> implementation — no external MVVM framework
/// dependency required. Supports both sync and async actions via an
/// <c>async void</c> overload (fire-and-forget from WPF).
/// </summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add
        {
            CommandManager.RequerySuggested += value;
        }

        remove
        {
            CommandManager.RequerySuggested -= value;
        }
    }

    public bool CanExecute(object? p)
    {
        return canExecute?.Invoke(p) ?? true;
    }

    public void Execute(object? p)
    {
        execute(p);
    }

    public static void RaiseRequery()
    {
        CommandManager.InvalidateRequerySuggested();
    }
}

/// <summary>
/// An async command, which by default cannot run again while its previous execution is still going.
/// <paramref name="allowsOverlap"/> lifts that for commands whose own <paramref name="canExecute"/> already guards
/// what matters — Run and Resume, which act per chain and may run for several chains at once.
/// </summary>
public sealed class AsyncRelayCommand(
    Func<object?, Task> execute, Func<object?, bool>? canExecute = null, bool allowsOverlap = false)
    : ICommand
{
    private int _running;

    public event EventHandler? CanExecuteChanged
    {
        add
        {
            CommandManager.RequerySuggested += value;
        }

        remove
        {
            CommandManager.RequerySuggested -= value;
        }
    }

    public bool CanExecute(object? p)
    {
        return (allowsOverlap || _running == 0) && (canExecute?.Invoke(p) ?? true);
    }

    public async void Execute(object? p)
    {
        _running++;
        CommandManager.InvalidateRequerySuggested();
        try { await execute(p); }
        finally
        {
            _running--;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
