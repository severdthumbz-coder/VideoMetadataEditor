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

    /// <summary>The content-type composition of the selected files, so the dialog can
    /// grey out fields that don't apply (e.g. Show title for a movie-only selection).</summary>
    public enum SelectionKind { Mixed, AllMovies, AllEpisodes }

    public BatchEditDialog(int selectedCount, SelectionKind kind = SelectionKind.Mixed, Window? owner = null)
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
        ApplySelectionKind(kind);
    }

    /// <summary>
    /// Greys out (disables + dims + unticks) fields that don't apply to the selection:
    ///   • Show title is meaningful only for TV episodes — disabled for an all-movie selection.
    ///   • Year is a movie-level field (episodes use aired date) — disabled for an all-episode selection.
    /// On a mixed selection nothing is greyed; the user is trusted to tick what's relevant.
    /// Fields shared by both types (Genre, Cast, Director, MPA, Watched) are never greyed.
    /// </summary>
    private void ApplySelectionKind(SelectionKind kind)
    {
        switch (kind)
        {
            case SelectionKind.AllMovies:
                DisableRow(ShowTitleCheck, ShowTitleInput,
                    "Show title applies to TV episodes — not to a movie-only selection.");
                break;
            case SelectionKind.AllEpisodes:
                DisableRow(YearCheck, YearInput,
                    "Year is a movie field — episodes use their aired date, which isn't batch-editable.");
                break;
            case SelectionKind.Mixed:
            default:
                // Leave everything enabled.
                break;
        }
    }

    /// <summary>Unticks, disables, and dims a checkbox + its input, with an explanatory tooltip.</summary>
    private static void DisableRow(System.Windows.Controls.CheckBox check,
        System.Windows.Controls.Control input, string reason)
    {
        check.IsChecked = false;
        check.IsEnabled = false;
        check.Opacity   = 0.45;
        check.ToolTip   = reason;
        input.IsEnabled = false;
        input.Opacity   = 0.45;
        input.ToolTip   = reason;
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
