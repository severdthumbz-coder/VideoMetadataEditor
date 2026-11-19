using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.ViewModels;

namespace VideoMetadataEditor.Views;

/// <summary>
/// Review queue (build 178): watch-folder files auto-embed didn't embed on its own, with
/// the candidate matches side by side — poster, title, year, overview, TMDB/IMDb ID — so
/// you can pick the right one. One click embeds it (and renames, as auto-embed would).
/// Modeless: new files keep arriving while it's open. Built in code, themed from the app's
/// resources, like History.
/// </summary>
public sealed class ReviewQueueWindow : Window
{
    private static ReviewQueueWindow? _open;

    private readonly MainViewModel _vm;
    private readonly ListBox _items = new();
    private readonly ListBox _candidates = new();
    private readonly TextBlock _reason = new();
    private readonly TextBlock _empty = new();
    private readonly Button _embed = new(), _search = new(), _skip = new(), _close = new();

    private const string ItemTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <StackPanel Margin="4,6">
            <TextBlock Text="{Binding What}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis"/>
            <TextBlock Text="{Binding FileName}" FontSize="11" Opacity="0.7" TextTrimming="CharacterEllipsis"/>
            <TextBlock Text="{Binding Reason}" FontSize="11" TextWrapping="Wrap" Foreground="{DynamicResource WarningBrush}"/>
          </StackPanel>
        </DataTemplate>
        """;

    private const string CandidateTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Grid Margin="4,6">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="70"/>
              <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>
            <Border Width="62" Height="92" Background="{DynamicResource SurfaceAltBrush}" CornerRadius="3" VerticalAlignment="Top">
              <Image Source="{Binding PosterUrl, TargetNullValue={x:Null}, FallbackValue={x:Null}}" Stretch="UniformToFill"
                     xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"/>
            </Border>
            <StackPanel Grid.Column="1" Margin="8,0,0,0">
              <TextBlock FontWeight="SemiBold" FontSize="14" TextWrapping="Wrap">
                <Run Text="{Binding Title, Mode=OneWay}"/><Run Text="  ("/><Run Text="{Binding Year, Mode=OneWay}"/><Run Text=")"/>
              </TextBlock>
              <TextBlock FontSize="11" Opacity="0.7" Margin="0,2,0,4">
                <Run Text="TMDB "/><Run Text="{Binding TmdbId, Mode=OneWay}"/><Run Text="   "/><Run Text="{Binding ImdbId, Mode=OneWay}"/>
              </TextBlock>
              <TextBlock Text="{Binding Overview}" TextWrapping="Wrap" FontSize="12" MaxHeight="70" TextTrimming="WordEllipsis"/>
            </StackPanel>
          </Grid>
        </DataTemplate>
        """;

    private ReviewQueueWindow(MainViewModel vm)
    {
        _vm = vm;
        Title = "Review queue — choose the right match";
        Width = 1000; Height = 640; MinWidth = 720; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        SetResourceReference(BackgroundProperty, "SurfaceBrush");
        SetResourceReference(ForegroundProperty, "ForegroundBrush");

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            Text = "These new files weren't embedded automatically, because more than one entry fits, the match " +
                   "wasn't confident, or you've chosen to pick movies yourself. Select a file, pick its match " +
                   "and click Embed. Clear-cut matches are still embedded automatically."
        };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "ForegroundMutedBrush");
        root.Children.Add(intro);

        // ── Files (left) and candidates (right) ──
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        root.Children.Add(body);

        StyleList(_items);
        _items.ItemTemplate = (DataTemplate)XamlReader.Parse(ItemTemplate);
        _items.ItemsSource = vm.ReviewQueue;
        _items.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_items, ScrollBarVisibility.Disabled);
        _items.SelectionChanged += (_, _) => ShowSelected();
        body.Children.Add(_items);

        var right = new DockPanel();
        Grid.SetColumn(right, 2);
        _reason.TextWrapping = TextWrapping.Wrap;
        _reason.FontWeight = FontWeights.SemiBold;
        _reason.Margin = new Thickness(0, 0, 0, 8);
        _reason.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
        DockPanel.SetDock(_reason, Dock.Top);
        right.Children.Add(_reason);

        StyleList(_candidates);
        _candidates.ItemTemplate = (DataTemplate)XamlReader.Parse(CandidateTemplate);
        _candidates.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        ScrollViewer.SetHorizontalScrollBarVisibility(_candidates, ScrollBarVisibility.Disabled);
        _candidates.SelectionChanged += (_, _) =>
        {
            if (_items.SelectedItem is ReviewItem item && _candidates.SelectedItem is SearchResult c) item.Selected = c;
            UpdateButtons();
        };
        _candidates.MouseDoubleClick += async (_, _) => await EmbedSelectedAsync();
        right.Children.Add(_candidates);
        body.Children.Add(right);

        _empty.Text = "Nothing to review — every new file was matched.";
        _empty.HorizontalAlignment = HorizontalAlignment.Center;
        _empty.VerticalAlignment = VerticalAlignment.Center;
        _empty.SetResourceReference(TextBlock.ForegroundProperty, "ForegroundMutedBrush");
        Grid.SetRow(_empty, 1);
        root.Children.Add(_empty);

        // ── Buttons ──
        var bar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        MakeButton(_search, "🔎 Search myself", "Select this file in the Files panel and open Retrieved Data to search by hand");
        MakeButton(_skip, "Skip", "Take it off the queue — the file stays in the Files panel, highlighted, for later");
        MakeButton(_embed, "✓ Embed this match", "Fetch the full details for the selected match, embed them and rename the file", primary: true);
        MakeButton(_close, "Close", null);
        _close.IsCancel = true;
        foreach (var b in new[] { _search, _skip, _embed, _close }) bar.Children.Add(b);
        Grid.SetRow(bar, 2);
        root.Children.Add(bar);

        _embed.Click  += async (_, _) => await EmbedSelectedAsync();
        _skip.Click   += (_, _) => { if (_items.SelectedItem is ReviewItem i) { _vm.SkipReview(i); SelectNext(); } };
        _search.Click += (_, _) => { if (_items.SelectedItem is ReviewItem i) { _vm.SearchReviewManually(i); Close(); } };
        _close.Click  += (_, _) => Close();

        vm.ReviewQueue.CollectionChanged += OnQueueChanged;
        Closed += (_, _) => { vm.ReviewQueue.CollectionChanged -= OnQueueChanged; _open = null; };

        Content = root;
        SelectNext();
    }

    private static void StyleList(ListBox lb)
    {
        lb.SetResourceReference(BackgroundProperty, "InputBackgroundBrush");
        lb.SetResourceReference(ForegroundProperty, "ForegroundBrush");
        lb.SetResourceReference(BorderBrushProperty, "BorderBrush");
    }

    private static void MakeButton(Button b, string text, string? tip, bool primary = false)
    {
        b.Content = text;
        b.Padding = new Thickness(14, 6, 14, 6);
        b.Margin = new Thickness(8, 0, 0, 0);
        if (tip != null) b.ToolTip = tip;
        if (Application.Current?.TryFindResource(primary ? "PrimaryButton" : "SecondaryButton") is Style s) b.Style = s;
    }

    private void OnQueueChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_items.SelectedItem == null) SelectNext();
        UpdateButtons();
    }

    private void SelectNext()
    {
        if (_items.SelectedItem == null && _vm.ReviewQueue.Count > 0) _items.SelectedIndex = 0;
        ShowSelected();
    }

    private void ShowSelected()
    {
        var item = _items.SelectedItem as ReviewItem;
        _candidates.ItemsSource = item?.Candidates;
        _candidates.SelectedItem = item?.Selected;
        if (_candidates.SelectedItem != null) _candidates.ScrollIntoView(_candidates.SelectedItem);
        _reason.Text = item == null ? string.Empty
            : item.Candidates.Count == 0
                ? $"{item.Reason}. No candidates — use Search myself."
                : $"{item.Reason}. Pick the right one:";
        bool any = _vm.ReviewQueue.Count > 0;
        _empty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        _items.Visibility = _candidates.Visibility = _reason.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var item = _items.SelectedItem as ReviewItem;
        _embed.IsEnabled  = item is { IsBusy: false } && _candidates.SelectedItem is SearchResult;
        _skip.IsEnabled   = item is { IsBusy: false };
        _search.IsEnabled = item is { IsBusy: false };
    }

    private async Task EmbedSelectedAsync()
    {
        if (_items.SelectedItem is not ReviewItem item || _candidates.SelectedItem is not SearchResult choice || item.IsBusy) return;
        _embed.IsEnabled = false;
        _embed.Content = "Embedding…";
        try
        {
            await _vm.ApproveReviewAsync(item, choice);
        }
        finally
        {
            _embed.Content = "✓ Embed this match";
            SelectNext();
        }
    }

    /// <summary>Opens the queue, or brings the open one to the front.</summary>
    public static void Open(MainViewModel vm)
    {
        if (_open != null) { _open.Activate(); return; }
        _open = new ReviewQueueWindow(vm);
        var owner = Application.Current?.MainWindow;
        if (owner != null && owner.IsVisible) _open.Owner = owner;
        _open.Show();
    }
}
