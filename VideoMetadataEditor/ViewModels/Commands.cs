using System.Windows.Input;

namespace VideoMetadataEditor.ViewModels;

/// <summary>
/// Lightweight ICommand implementations used throughout the ViewModels.
/// Kept in a dedicated file so all ViewModels (MainViewModel, DuplicatesViewModel)
/// can reference them without cross-class visibility issues.
///
/// CanExecute re-evaluation is routed through <see cref="CommandRequery"/> rather than
/// WPF's CommandManager directly, so these classes carry no hard WPF dependency beyond
/// the cross-platform <see cref="ICommand"/> interface. On WPF the installed provider is
/// CommandManager-backed, so behaviour is identical to before (automatic requery on UI
/// input). <see cref="ICommand"/> itself is shared across WPF and Avalonia.
/// </summary>
public class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add    => CommandRequery.Provider.AddRequeryHandler(value!);
        remove => CommandRequery.Provider.RemoveRequeryHandler(value!);
    }
    public bool CanExecute(object? p) => canExecute?.Invoke(p) ?? true;
    public void Execute(object? p) => execute(p);
}

public class AsyncRelayCommand(Func<Task> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged
    {
        add    => CommandRequery.Provider.AddRequeryHandler(value!);
        remove => CommandRequery.Provider.RemoveRequeryHandler(value!);
    }
    public bool CanExecute(object? p) => !_running && (canExecute?.Invoke(p) ?? true);
    public async void Execute(object? p)
    {
        _running = true;
        CommandRequery.Invalidate();
        try { await execute(); }
        finally
        {
            _running = false;
            CommandRequery.Invalidate();
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
        add    => CommandRequery.Provider.AddRequeryHandler(value!);
        remove => CommandRequery.Provider.RemoveRequeryHandler(value!);
    }
    public bool CanExecute(object? p) => !_running && (canExecute?.Invoke(p) ?? true);
    public async void Execute(object? p)
    {
        _running = true;
        CommandRequery.Invalidate();
        try { await execute(p is T t ? t : default); }
        finally
        {
            _running = false;
            CommandRequery.Invalidate();
        }
    }
}
