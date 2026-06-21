namespace VideoMetadataEditor.Services;

/// <summary>Buttons offered by a message dialog (platform-neutral).</summary>
public enum DialogButtons { Ok, OkCancel, YesNo, YesNoCancel }

/// <summary>Severity/icon hint for a message dialog (platform-neutral).</summary>
public enum DialogIcon { None, Information, Warning, Error, Question }

/// <summary>The user's response to a message dialog (platform-neutral).</summary>
public enum DialogResult { Ok, Cancel, Yes, No }

/// <summary>
/// Abstraction over user-facing dialogs (message boxes, file/folder pickers) so
/// ViewModels don't reference WPF's <c>System.Windows.MessageBox</c> or
/// <c>Microsoft.Win32</c> dialogs directly. The WPF app supplies a concrete
/// implementation; a future Avalonia/MAUI host supplies its own. Headless/test contexts
/// use <see cref="NullDialogService"/>, which auto-answers and cancels pickers.
/// </summary>
public interface IDialogService
{
    /// <summary>Show a message box and return which button the user chose.</summary>
    DialogResult Show(string message, string title,
        DialogButtons buttons = DialogButtons.Ok, DialogIcon icon = DialogIcon.None);

    /// <summary>Pick a single existing folder. Returns null if cancelled.</summary>
    string? PickFolder(string title, string? initialDirectory = null);

    /// <summary>Pick a single existing file. Returns null if cancelled.</summary>
    string? PickOpenFile(string title, string filter, string? initialDirectory = null);

    /// <summary>Pick a destination file path for saving. Returns null if cancelled.</summary>
    string? PickSaveFile(string title, string filter, string? suggestedFileName = null,
        string? initialDirectory = null);
}

/// <summary>
/// Headless default: message dialogs return a safe "negative" answer (Cancel/No) so
/// nothing destructive proceeds without a real UI, and pickers return null (cancelled).
/// </summary>
public sealed class NullDialogService : IDialogService
{
    public static readonly NullDialogService Instance = new();

    public DialogResult Show(string message, string title,
        DialogButtons buttons = DialogButtons.Ok, DialogIcon icon = DialogIcon.None)
        => buttons switch
        {
            DialogButtons.Ok => DialogResult.Ok,
            _                => DialogResult.Cancel, // never auto-confirm a destructive prompt
        };

    public string? PickFolder(string title, string? initialDirectory = null) => null;
    public string? PickOpenFile(string title, string filter, string? initialDirectory = null) => null;
    public string? PickSaveFile(string title, string filter, string? suggestedFileName = null,
        string? initialDirectory = null) => null;
}
