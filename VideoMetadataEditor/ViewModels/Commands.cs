using System.Windows.Input;

namespace VideoMetadataEditor.ViewModels;

/// <summary>
/// Lightweight ICommand implementations used throughout the ViewModels.
/// Kept in a dedicated file so all ViewModels (MainViewModel, DuplicatesViewModel)
/// can reference them without cross-class visibility issues.
/// </summary>
public class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add    => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }
    public bool CanExecute(object? p) => canExecute?.Invoke(p) ?? true;
    public void Execute(object? p) => execute(p);
}

public class AsyncRelayCommand(Func<Task> execute, Func<object?, bool>? canExecute = null) : ICommand
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
        try { await execute(); }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

/// <summary>Async command that passes a typed parameter to the execute function.</summary>
public class AsyncRelayCommand<T>(
    Func<T?, Task> execute,
    Func<object?, bool>? canExecute = null) : ICommand
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
        try { await execute(p is T t ? t : default); }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
