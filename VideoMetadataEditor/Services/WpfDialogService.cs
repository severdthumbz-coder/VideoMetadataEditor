using System.Windows;
using Microsoft.Win32;

namespace VideoMetadataEditor.Services;

/// <summary>
/// WPF implementation of <see cref="IDialogService"/>. Platform glue: the only place that
/// references <c>System.Windows.MessageBox</c> and the <c>Microsoft.Win32</c> dialogs for
/// the ViewModels that have been migrated to the abstraction. A future Avalonia/MAUI host
/// supplies its own implementation of the same interface.
/// </summary>
public sealed class WpfDialogService : IDialogService
{
    public DialogResult Show(string message, string title,
        DialogButtons buttons = DialogButtons.Ok, DialogIcon icon = DialogIcon.None)
    {
        var wpfButton = buttons switch
        {
            DialogButtons.OkCancel    => MessageBoxButton.OKCancel,
            DialogButtons.YesNo       => MessageBoxButton.YesNo,
            DialogButtons.YesNoCancel => MessageBoxButton.YesNoCancel,
            _                         => MessageBoxButton.OK,
        };
        var wpfIcon = icon switch
        {
            DialogIcon.Information => MessageBoxImage.Information,
            DialogIcon.Warning     => MessageBoxImage.Warning,
            DialogIcon.Error       => MessageBoxImage.Error,
            DialogIcon.Question    => MessageBoxImage.Question,
            _                      => MessageBoxImage.None,
        };

        var result = MessageBox.Show(message, title, wpfButton, wpfIcon);
        return result switch
        {
            MessageBoxResult.OK     => DialogResult.Ok,
            MessageBoxResult.Yes    => DialogResult.Yes,
            MessageBoxResult.No     => DialogResult.No,
            _                       => DialogResult.Cancel,
        };
    }

    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dlg = new OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
            dlg.InitialDirectory = initialDirectory;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public string? PickOpenFile(string title, string filter, string? initialDirectory = null)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
            dlg.InitialDirectory = initialDirectory;
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? PickSaveFile(string title, string filter, string? suggestedFileName = null,
        string? initialDirectory = null)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter };
        if (!string.IsNullOrWhiteSpace(suggestedFileName)) dlg.FileName = suggestedFileName;
        if (!string.IsNullOrWhiteSpace(initialDirectory) && System.IO.Directory.Exists(initialDirectory))
            dlg.InitialDirectory = initialDirectory;
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
