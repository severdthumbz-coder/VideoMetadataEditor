namespace VideoMetadataEditor.Services;

/// <summary>
/// WPF implementation of <see cref="IClipboardService"/>. Platform glue: the only place
/// that references <c>System.Windows.Clipboard</c> for the migrated ViewModels. The
/// clipboard can intermittently fail if another process holds it open, so failures are
/// caught and reported as false rather than thrown.
/// </summary>
public sealed class WpfClipboardService : IClipboardService
{
    public bool SetText(string text)
    {
        try { System.Windows.Clipboard.SetText(text); return true; }
        catch { return false; }
    }
}
