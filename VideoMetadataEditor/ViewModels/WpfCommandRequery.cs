using System.Windows.Input;

namespace VideoMetadataEditor.ViewModels;

/// <summary>
/// WPF implementation of <see cref="ICommandRequeryProvider"/>, backed by WPF's
/// <see cref="CommandManager"/>. This preserves the existing behaviour exactly: command
/// CanExecute is re-evaluated automatically on UI input, so buttons enable/disable
/// without explicit notification. This is the only command-side reference to WPF's
/// CommandManager; everything else goes through the <see cref="ICommandRequeryProvider"/>
/// seam.
///
/// Installed once at application startup:
///     CommandRequery.Provider = new WpfCommandRequery();
/// </summary>
public sealed class WpfCommandRequery : ICommandRequeryProvider
{
    public void AddRequeryHandler(EventHandler handler) =>
        CommandManager.RequerySuggested += handler;

    public void RemoveRequeryHandler(EventHandler handler) =>
        CommandManager.RequerySuggested -= handler;

    public void Invalidate() =>
        CommandManager.InvalidateRequerySuggested();
}
