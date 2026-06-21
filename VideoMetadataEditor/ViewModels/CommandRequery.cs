namespace VideoMetadataEditor.ViewModels;

/// <summary>
/// Seam for the "re-evaluate CanExecute for all commands" mechanism, so the RelayCommand
/// family doesn't reference WPF's <c>CommandManager</c> directly.
///
/// On WPF this is backed by <c>CommandManager.RequerySuggested</c> /
/// <c>InvalidateRequerySuggested()</c>, which fires automatically on UI input — the
/// behaviour the whole app relies on for buttons to enable/disable without explicit
/// notification. A future Avalonia/MAUI host installs its own provider; headless/test
/// contexts use the default no-op provider.
///
/// IMPORTANT: the WPF host MUST install <c>WpfCommandRequery</c> at startup so behaviour
/// is identical to before. If nothing is installed, commands simply won't auto-requery
/// (safe, but buttons won't update on their own) — which is the correct default for a
/// non-WPF process.
/// </summary>
public interface ICommandRequeryProvider
{
    /// <summary>Subscribe a handler to the global "requery suggested" signal.</summary>
    void AddRequeryHandler(EventHandler handler);

    /// <summary>Unsubscribe a handler from the global "requery suggested" signal.</summary>
    void RemoveRequeryHandler(EventHandler handler);

    /// <summary>Ask all commands to re-evaluate CanExecute now.</summary>
    void Invalidate();
}

/// <summary>Default no-op provider (headless/test). Commands won't auto-requery.</summary>
public sealed class NullCommandRequery : ICommandRequeryProvider
{
    public static readonly NullCommandRequery Instance = new();
    public void AddRequeryHandler(EventHandler handler) { }
    public void RemoveRequeryHandler(EventHandler handler) { }
    public void Invalidate() { }
}

/// <summary>
/// Global access point for the active requery provider. Set once at startup by the
/// platform host (the WPF app sets <see cref="Provider"/> to a WpfCommandRequery).
/// </summary>
public static class CommandRequery
{
    public static ICommandRequeryProvider Provider { get; set; } = NullCommandRequery.Instance;

    public static void Invalidate() => Provider.Invalidate();
}
