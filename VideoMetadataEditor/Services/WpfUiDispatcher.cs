using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace VideoMetadataEditor.Services;

/// <summary>
/// WPF implementation of <see cref="IUiDispatcher"/>, backed by the application's
/// <see cref="Dispatcher"/>. This is platform glue: it stays in the WPF project and is
/// the only place that references the WPF Dispatcher / CommandManager. A future Avalonia
/// or MAUI host provides its own implementation of the same interface.
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>Convenience factory using the current application's dispatcher.</summary>
    public static WpfUiDispatcher FromCurrent() =>
        new(Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);

    public bool IsOnUiThread => _dispatcher.CheckAccess();

    public void Post(Action action)
    {
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(DispatcherPriority.Normal, action);
    }

    public void InvalidateCommands() =>
        _dispatcher.BeginInvoke(DispatcherPriority.Normal,
            new Action(CommandManager.InvalidateRequerySuggested));
}
