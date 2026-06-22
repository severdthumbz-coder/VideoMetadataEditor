namespace VideoMetadataEditor.Services;

/// <summary>
/// Abstraction over clipboard access so ViewModels don't reference WPF's
/// <c>System.Windows.Clipboard</c> directly. The WPF app supplies a concrete
/// implementation; a future Avalonia/MAUI host supplies its own. Headless/test contexts
/// use <see cref="NullClipboardService"/>, which silently does nothing.
/// </summary>
public interface IClipboardService
{
    /// <summary>Copy plain text to the clipboard. Returns true on success.</summary>
    bool SetText(string text);
}

/// <summary>Headless default: no clipboard available, reports failure without throwing.</summary>
public sealed class NullClipboardService : IClipboardService
{
    public static readonly NullClipboardService Instance = new();
    public bool SetText(string text) => false;
}
