using System.Windows;
using System.Windows.Controls;
using VideoMetadataEditor.Services;

namespace VideoMetadataEditor.Views;

/// <summary>
/// Collects a set of shared field edits to apply across selected files.
/// On Apply, <see cref="Result"/> is populated with only the ticked fields.
/// </summary>
public partial class BatchEditDialog : Window
{
    /// <summary>The edits chosen by the user. Null until Apply is clicked.</summary>
    public BatchFieldEditService.BatchFieldEdits? Result { get; private set; }

    public BatchEditDialog(int selectedCount, Window? owner = null)
    {
        InitializeComponent();

        if (owner != null)
        {
            Owner = owner;
            // Inherit owner theming.
            Resources.MergedDictionaries.Clear();
            foreach (var dict in owner.Resources.MergedDictionaries)
                Resources.MergedDictionaries.Add(dict);
        }

        HeaderText.Text = $"Apply shared field values to {selectedCount} selected file(s)";
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var edits = new BatchFieldEditService.BatchFieldEdits();

        if (GenreCheck.IsChecked == true)
        {
            edits.Genre     = GenreInput.Text ?? string.Empty;
            edits.GenreMode = ModeOf(GenreMode);
        }
        if (CastCheck.IsChecked == true)
        {
            edits.Cast     = CastInput.Text ?? string.Empty;
            edits.CastMode = ModeOf(CastMode);
        }
        if (DirectorCheck.IsChecked == true)
            edits.Director = DirectorInput.Text ?? string.Empty;
        if (MpaCheck.IsChecked == true)
            edits.MpaRating = MpaInput.Text ?? string.Empty;
        if (YearCheck.IsChecked == true)
            edits.Year = YearInput.Text ?? string.Empty;
        if (ShowTitleCheck.IsChecked == true)
            edits.ShowTitle = ShowTitleInput.Text ?? string.Empty;
        if (WatchedCheck.IsChecked == true)
            edits.Watched = WatchedMode.SelectedIndex == 1
                ? BatchFieldEditService.WatchedChange.SetUnwatched
                : BatchFieldEditService.WatchedChange.SetWatched;

        if (!edits.HasAnyChange)
        {
            MessageBox.Show(this,
                "No fields are ticked. Tick at least one field to change, or click Cancel.",
                "Nothing to apply", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Result = edits;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static BatchFieldEditService.TextMode ModeOf(ComboBox combo)
        => combo.SelectedIndex == 1
            ? BatchFieldEditService.TextMode.Append
            : BatchFieldEditService.TextMode.Replace;
}
