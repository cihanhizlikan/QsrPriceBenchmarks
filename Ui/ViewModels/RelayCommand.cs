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
        add    => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? p) => canExecute?.Invoke(p) ?? true;
    public void Execute(object? p)    => execute(p);

    public static void RaiseRequery() => CommandManager.InvalidateRequerySuggested();
}

public sealed class AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    : ICommand
{
    private bool _running;

    public event EventHandler? CanExecuteChanged
    {
        add    => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? p) => !_running && (canExecute?.Invoke(p) ?? true);

    public async void Execute(object? p)
    {
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try   { await execute(p); }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
