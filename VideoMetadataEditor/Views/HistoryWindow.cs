using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using VideoMetadataEditor.Services;

namespace VideoMetadataEditor.Views;

/// <summary>
/// Log → 🕘 History (build 174): everything VME has done to files, newest first, kept
/// across restarts in history.jsonl. Select an entry to see each file it changed;
/// renames, moves and extension fixes can be undone. Undo never overwrites anything —
/// a file that has since moved, or whose old name is now taken, is skipped and listed.
/// </summary>
public sealed class HistoryWindow : Window
{
    private sealed class EntryRow
    {
        public required OperationJournal.Record Rec { get; init; }
        /// <summary>Build 175: reversible items not put back yet (a partial undo lowers this).</summary>
        public required int Remaining { get; init; }
        public bool   CanUndo => Rec.CanUndo && Remaining > 0;
        public bool   Partly  => CanUndo && Remaining < Rec.ReversibleItems.Count();
        public string When    => Rec.When.ToString("yyyy-MM-dd  HH:mm");
        public string Kind    => Rec.Kind;
        public string Summary => Rec.Summary;
        public int    Files   => Rec.Items.Count;
        public string Status  => Rec.UndoOf != null ? "↩ undo"
                               : Rec.Undone || (Rec.CanUndo && Remaining == 0) ? "undone"
                               : Partly ? "partly undone"
                               : CanUndo ? "can undo"
                               : string.Empty;
    }

    private readonly DataGrid _entries = new();
    private readonly DataGrid _items = new();
    private readonly Button _undo = new();
    private readonly TextBlock _hint = new();
    private readonly Action<List<OperationJournal.Item>>? _afterUndo;

    private HistoryWindow(Action<List<OperationJournal.Item>>? afterUndo)
    {
        _afterUndo = afterUndo;
        Title = "History — changes VME made to your files";
        Width = 1100; Height = 680; MinWidth = 720; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "ForegroundBrush");

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = "Every rename, move, copy and fix is recorded here and kept between sessions. " +
                   "Renames, moves and extension fixes can be undone: files are put back where they were " +
                   "(with their subtitles, .nfo and artwork), and nothing is ever overwritten."
        };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "ForegroundMutedBrush");
        root.Children.Add(intro);

        StyleGrid(_entries);
        _entries.SelectionMode = DataGridSelectionMode.Single;
        _entries.Columns.Add(Col("When", nameof(EntryRow.When), 140));
        _entries.Columns.Add(Col("Operation", nameof(EntryRow.Kind), 120));
        _entries.Columns.Add(Col("Summary", nameof(EntryRow.Summary), 0));
        _entries.Columns.Add(Col("Files", nameof(EntryRow.Files), 60));
        _entries.Columns.Add(Col("Status", nameof(EntryRow.Status), 90));
        _entries.SelectionChanged += (_, _) => ShowSelected();
        Grid.SetRow(_entries, 1);
        root.Children.Add(_entries);

        var detailsHeader = new TextBlock
        {
            Text = "Files in the selected entry", FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 6)
        };
        Grid.SetRow(detailsHeader, 2);
        root.Children.Add(detailsHeader);

        StyleGrid(_items);
        _items.Columns.Add(Col("Change", nameof(OperationJournal.Item.Kind), 100));
        _items.Columns.Add(Col("From", nameof(OperationJournal.Item.From), 0));
        _items.Columns.Add(Col("To", nameof(OperationJournal.Item.To), 0));
        Grid.SetRow(_items, 3);
        root.Children.Add(_items);

        // ── Buttons ──
        var bar = new DockPanel { Margin = new Thickness(0, 12, 0, 0), LastChildFill = true };
        var openFile = new Button { Content = "📄 Open history file", Padding = new Thickness(12, 6, 12, 6) };
        var close    = new Button { Content = "Close", Padding = new Thickness(16, 6, 16, 6), IsCancel = true, Margin = new Thickness(8, 0, 0, 0) };
        _undo.Content = "↩ Undo this";
        _undo.Padding = new Thickness(16, 6, 16, 6);
        _undo.Margin  = new Thickness(8, 0, 0, 0);
        _undo.IsEnabled = false;
        if (Application.Current?.TryFindResource("SecondaryButton") is Style secondary) { openFile.Style = secondary; close.Style = secondary; }
        if (Application.Current?.TryFindResource("PrimaryButton") is Style primary) _undo.Style = primary;

        openFile.Click += (_, _) =>
        {
            try
            {
                if (File.Exists(OperationJournal.FilePath))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{OperationJournal.FilePath}\"") { UseShellExecute = true });
            }
            catch { /* Explorer unavailable — harmless */ }
        };
        close.Click += (_, _) => Close();
        _undo.Click += (_, _) => UndoSelected();

        DockPanel.SetDock(openFile, Dock.Left);
        bar.Children.Add(openFile);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _hint.VerticalAlignment = VerticalAlignment.Center;
        _hint.Margin = new Thickness(0, 0, 8, 0);
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "ForegroundMutedBrush");
        right.Children.Add(_hint);
        right.Children.Add(_undo);
        right.Children.Add(close);
        bar.Children.Add(right);
        Grid.SetRow(bar, 4);
        root.Children.Add(bar);

        Content = root;
        Reload();
    }

    private static DataGridTextColumn Col(string header, string path, double width) => new()
    {
        Header = header, Binding = new Binding(path),
        Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star)
    };

    private static void StyleGrid(DataGrid g)
    {
        g.AutoGenerateColumns = false; g.IsReadOnly = true; g.CanUserAddRows = false;
        g.HeadersVisibility = DataGridHeadersVisibility.Column;
        g.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
        g.SetResourceReference(BackgroundProperty, "InputBackgroundBrush");
        g.SetResourceReference(ForegroundProperty, "ForegroundBrush");
        g.SetResourceReference(BorderBrushProperty, "BorderBrush");
        g.SetResourceReference(DataGrid.RowBackgroundProperty, "InputBackgroundBrush");
        g.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "BorderBrush");
    }

    private void Reload(string? selectId = null)
    {
        var all  = OperationJournal.Load();
        var rows = all.Select(r => new EntryRow
        {
            Rec = r,
            Remaining = r.CanUndo ? OperationJournal.RemainingItems(r, all).Count : 0
        }).ToList();
        _entries.ItemsSource = rows;
        var pick = selectId != null ? rows.FirstOrDefault(r => r.Rec.Id == selectId) : rows.FirstOrDefault();
        _entries.SelectedItem = pick;
        if (rows.Count == 0) _hint.Text = "Nothing recorded yet.";
        ShowSelected();
    }

    private void ShowSelected()
    {
        var row = _entries.SelectedItem as EntryRow;
        _items.ItemsSource = row?.Rec.Items;
        _undo.IsEnabled = row?.CanUndo == true;
        if (row == null) return;
        _hint.Text = row.CanUndo
            ? row.Partly
                ? $"{row.Remaining} of {row.Rec.ReversibleItems.Count()} file(s) still to put back"
                : $"{row.Remaining} file(s) can be put back"
            : row.Rec.Undone || (row.Rec.CanUndo && row.Remaining == 0) ? "Already undone"
            : row.Rec.UndoOf != null ? "This entry is itself an undo"
            : "This kind of change can't be undone from here (in-place rewrites and copies)";
    }

    private void UndoSelected()
    {
        if (_entries.SelectedItem is not EntryRow row || !row.CanUndo) return;
        var count = row.Remaining;

        var answer = MessageBox.Show(this,
            $"Put {count} file(s) back where they were before:\n\n“{row.Rec.Summary}”\n\n" +
            "Subtitles, .nfo and artwork with the same name go back with them. A file that has " +
            "moved since, or whose old name is now taken, is skipped — nothing is overwritten.",
            "Undo", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        var outcome = OperationJournal.Undo(row.Rec);

        var put = row.Rec.ReversibleItems.ToList();
        _afterUndo?.Invoke(put);

        var msg = $"Put back {outcome.Restored} of {count} file(s).";
        if (outcome.Skipped.Count > 0)
            msg += "\n\nSkipped:\n" + string.Join("\n", outcome.Skipped.Take(15)) +
                   (outcome.Skipped.Count > 15 ? $"\n…and {outcome.Skipped.Count - 15} more" : string.Empty);
        MessageBox.Show(this, msg, "Undo complete",
            MessageBoxButton.OK, outcome.Skipped.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);

        Reload(row.Rec.Id);
    }

    /// <summary>
    /// Opens the History window. <paramref name="afterUndo"/> receives the items that
    /// were reversed (From = original path, To = where VME had put it) so the caller can
    /// update its lists.
    /// </summary>
    public static void Open(Action<List<OperationJournal.Item>>? afterUndo)
    {
        var w = new HistoryWindow(afterUndo);
        var owner = Application.Current?.MainWindow;
        if (owner != null && owner.IsVisible) w.Owner = owner;
        w.ShowDialog();
    }
}
