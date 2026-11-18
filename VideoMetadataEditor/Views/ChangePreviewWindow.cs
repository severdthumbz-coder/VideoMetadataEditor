using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace VideoMetadataEditor.Views;

/// <summary>
/// "Here's exactly what will change" dialog shown before Batch Rename, Move and Copy
/// (build 174). One row per file: current name → new name or destination, plus a warning
/// when something needs attention (a name already taken, two files that would get the
/// same name, a blocked rename). Nothing is changed until the user confirms.
///
/// Built in code (no XAML) and styled from the app's theme resources so it follows
/// light/dark mode.
/// </summary>
public sealed class ChangePreviewWindow : Window
{
    public sealed record Row(string From, string To, string Note)
    {
        public bool HasNote => !string.IsNullOrEmpty(Note) && Note != "Unchanged";
    }

    private ChangePreviewWindow(string title, string summary, string fromHeader, string toHeader,
                                IReadOnlyList<Row> rows, string confirmText)
    {
        Title = title;
        Width = 980; Height = 600; MinWidth = 640; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "ForegroundBrush");

        var root = new DockPanel { Margin = new Thickness(16) };

        // ── Summary ──
        var header = new TextBlock
        {
            Text = summary, TextWrapping = TextWrapping.Wrap, FontSize = 13,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // ── Filter: only rows needing attention ──
        int attention = rows.Count(r => r.HasNote);
        var onlyProblems = new CheckBox
        {
            Content = $"Show only files that need attention ({attention})",
            Margin = new Thickness(0, 0, 0, 8),
            IsEnabled = attention > 0
        };
        onlyProblems.SetResourceReference(ForegroundProperty, "ForegroundBrush");
        DockPanel.SetDock(onlyProblems, Dock.Top);
        root.Children.Add(onlyProblems);

        // ── Buttons ──
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 6, 16, 6), IsCancel = true };
        var ok = new Button
        {
            Content = $"{confirmText} ({rows.Count(r => r.To != "—" && r.Note != "Unchanged")})",
            Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0), IsDefault = true
        };
        if (Application.Current?.TryFindResource("SecondaryButton") is Style secondary) cancel.Style = secondary;
        if (Application.Current?.TryFindResource("PrimaryButton") is Style primary) ok.Style = primary;
        ok.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        // ── List ──
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column,
            CanUserAddRows = false, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            SelectionMode = DataGridSelectionMode.Extended, BorderThickness = new Thickness(1)
        };
        grid.SetResourceReference(BackgroundProperty, "InputBackgroundBrush");
        grid.SetResourceReference(ForegroundProperty, "ForegroundBrush");
        grid.SetResourceReference(BorderBrushProperty, "BorderBrush");
        grid.SetResourceReference(DataGrid.RowBackgroundProperty, "InputBackgroundBrush");
        grid.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "BorderBrush");

        grid.Columns.Add(new DataGridTextColumn
            { Header = fromHeader, Binding = new Binding(nameof(Row.From)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn
            { Header = "", Binding = new Binding { Source = "→" }, Width = DataGridLength.Auto });
        grid.Columns.Add(new DataGridTextColumn
            { Header = toHeader, Binding = new Binding(nameof(Row.To)), Width = new DataGridLength(1.3, DataGridLengthUnitType.Star) });

        var noteColumn = new DataGridTextColumn
        {
            Header = "Note", Binding = new Binding(nameof(Row.Note)),
            Width = new DataGridLength(0.9, DataGridLengthUnitType.Star)
        };
        var noteStyle = new Style(typeof(TextBlock));
        noteStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        var warn = new DataTrigger { Binding = new Binding(nameof(Row.HasNote)), Value = true };
        warn.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("WarningBrush")));
        warn.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        noteStyle.Triggers.Add(warn);
        noteColumn.ElementStyle = noteStyle;
        grid.Columns.Add(noteColumn);

        grid.ItemsSource = rows;
        onlyProblems.Checked   += (_, _) => grid.ItemsSource = rows.Where(r => r.HasNote).ToList();
        onlyProblems.Unchecked += (_, _) => grid.ItemsSource = rows;

        root.Children.Add(grid);   // fills the remaining space
        Content = root;
    }

    /// <summary>Shows the preview; true only if the user clicks the confirm button.</summary>
    public static bool Confirm(string title, string summary, string fromHeader, string toHeader,
                               IReadOnlyList<Row> rows, string confirmText)
    {
        if (rows.Count == 0) return true;
        var w = new ChangePreviewWindow(title, summary, fromHeader, toHeader, rows, confirmText);
        var owner = Application.Current?.MainWindow;
        if (owner != null && owner.IsVisible) w.Owner = owner;
        return w.ShowDialog() == true;
    }
}
