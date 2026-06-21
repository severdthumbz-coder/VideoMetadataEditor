namespace VideoMetadataEditor.Services;

/// <summary>
/// Abstraction over the UI-thread marshaller so ViewModels don't depend directly on
/// WPF's <c>System.Windows.Threading.Dispatcher</c>. The WPF app supplies a Dispatcher-
/// backed implementation; a future Avalonia/MAUI host would supply its own. This is the
/// last piece that keeps the extracted ViewModels from compiling in a platform-agnostic
/// Core project (Phase 3).
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Run <paramref name="action"/> on the UI thread (async, fire-and-forget).</summary>
    void Post(Action action);

    /// <summary>True if the caller is already on the UI thread.</summary>
    bool IsOnUiThread { get; }

    /// <summary>
    /// Ask the command framework to re-evaluate CanExecute for all commands. On WPF this
    /// maps to CommandManager.InvalidateRequerySuggested, marshalled to the UI thread.
    /// </summary>
    void InvalidateCommands();
}

/// <summary>
/// A no-op dispatcher used as a safe default (e.g. in unit tests or before a real UI
/// dispatcher is installed). Runs actions inline on the calling thread.
/// </summary>
public sealed class NullUiDispatcher : IUiDispatcher
{
    public static readonly NullUiDispatcher Instance = new();
    public void Post(Action action) => action();
    public bool IsOnUiThread => true;
    public void InvalidateCommands() { /* no command framework in headless contexts */ }
}
