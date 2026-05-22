using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Input;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;

namespace VideoMetadataEditor.ViewModels;

/// <summary>
/// Owns all library state: entries, tabs, columns, scan, watch folders, export.
/// </summary>
public class LibraryViewModel : ViewModelBase
{
    private readonly AppSettings       _settings;
    private readonly LibraryScanService _scanService;
    private readonly LibraryCacheService _cacheService;
    private readonly MultiWatchFolderService _watchService;

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action<LibraryEntry>? FileDoubleClicked;

    // ── Library entries + view ────────────────────────────────────────────────
    public ObservableCollection<LibraryEntry> Entries { get; } = new();
    public ObservableCollection<LibraryTab>   Tabs    { get; } = new();
    public ObservableCollection<LibraryColumn> Columns { get; } = new();
    public ObservableCollection<string>       ExtraFolders { get; } = new();

    private ICollectionView? _view;
    public ICollectionView? View
    {
        get => _view;
        private set => Set(ref _view, value);
    }

    private LibraryTab? _selectedTab;
    public LibraryTab? SelectedTab
    {
        get => _selectedTab;
        set
        {
            Set(ref _selectedTab, value);
            RaiseProperty(nameof(ActiveTabIsTv));
            RaiseProperty(nameof(HasMultipleTabs));
            if (value != null) { _filterText = value.FilterText; RaiseProperty(nameof(FilterText)); }
            View?.Refresh();
            RaiseProperty(nameof(FilterCount));
            // Notify coordinator to sync column visibility
            TabChanged?.Invoke(ActiveTabIsTv);
        }
    }

    // Callback: coordinator calls SyncLibraryColumnVisibility after tab switch
    public Action<bool>? TabChanged { get; set; }

    public bool HasMultipleTabs => _settings.LibraryTabbedMode && Tabs.Count >= 1;
    public bool ActiveTabIsTv
    {
        get
        {
            if (_selectedTab == null) return false;
            var path = _selectedTab.FolderPath.ToUpperInvariant();
            if (path.Contains("TV") || path.Contains("SHOW") || path.Contains("SERIE")) return true;
            var entries = _selectedTab.Entries;
            if (entries.Count == 0) return false;
            return entries.Count(e => e.IsEpisode) > entries.Count / 2;
        }
    }

    // ── Scan state ────────────────────────────────────────────────────────────
    private bool _isScanning;
    public bool IsScanning { get => _isScanning; set { Set(ref _isScanning, value); CommandManager.InvalidateRequerySuggested(); } }
    public bool CanScan    => !IsScanning;

    private string _scanStatus = string.Empty;
    public string ScanStatus { get => _scanStatus; set => Set(ref _scanStatus, value); }

    private string _cacheInfo = string.Empty;
    public string CacheInfo { get => _cacheInfo; set => Set(ref _cacheInfo, value); }

    // ── Filter ────────────────────────────────────────────────────────────────
    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set { Set(ref _filterText, value); if (_selectedTab != null) _selectedTab.FilterText = value; View?.Refresh(); RaiseProperty(nameof(FilterCount)); }
    }

    public int FilterCount => View?.Cast<object>().Count() ?? 0;

    // ── Watch ─────────────────────────────────────────────────────────────────
    public bool WatchEnabled
    {
        get => _settings.LibraryWatchEnabled;
        set { _settings.LibraryWatchEnabled = value; RaiseProperty(); ApplyWatchSetting(); _ = App.ConfigService.SaveAsync(); }
    }

    public bool TabbedMode
    {
        get => _settings.LibraryTabbedMode;
        set { _settings.LibraryTabbedMode = value; RaiseProperty(); RaiseProperty(nameof(HasMultipleTabs)); _ = App.ConfigService.SaveAsync(); }
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    public ICommand ScanCommand              { get; }
    public ICommand ExportCsvCommand         { get; }
    public ICommand ExportXlsxCommand        { get; }
    public ICommand ClearCacheCommand        { get; }
    public ICommand SaveColumnLayoutCommand  { get; }
    public ICommand BrowseFolderCommand      { get; }
    public ICommand AddExtraFolderCommand    { get; }
    public ICommand CloseTabCommand          { get; }
    public ICommand RowDoubleClickCommand    { get; }
    public ICommand LoadSelectedCommand      { get; }

    // Delegates set by MainViewModel
    public Func<Task>?              ScanAsync               { get; set; }
    public Func<Task>?              ExportCsvAsync          { get; set; }
    public Func<Task>?              ExportXlsxAsync         { get; set; }
    public Action?                  BrowseFolder            { get; set; }
    public Action<LibraryEntry>?    LoadEntryIntoPanel      { get; set; }
    public Action<IList<object>>?   LoadSelectionIntoPanel  { get; set; }
    public Action?                  SaveColumnLayout        { get; set; }

    public LibraryViewModel(AppSettings settings, LibraryScanService scanService,
        LibraryCacheService cacheService, MultiWatchFolderService watchService)
    {
        _settings     = settings;
        _scanService  = scanService;
        _cacheService = cacheService;
        _watchService = watchService;

        // Restore extra folders
        foreach (var f in settings.ExtraLibraryFolders ?? Enumerable.Empty<string>())
            ExtraFolders.Add(f);

        // Set up collection view
        View = CollectionViewSource.GetDefaultView(Entries);
        if (View is ListCollectionView lcv)
        {
            lcv.Filter = o => o is LibraryEntry e && PassesFilter(e);
            lcv.SortDescriptions.Add(new SortDescription(nameof(LibraryEntry.Title), ListSortDirection.Ascending));
        }

        ScanCommand             = new AsyncRelayCommand(() => ScanAsync?.Invoke() ?? Task.CompletedTask, _ => CanScan);
        ExportCsvCommand        = new AsyncRelayCommand(() => ExportCsvAsync?.Invoke() ?? Task.CompletedTask, _ => Entries.Any());
        ExportXlsxCommand       = new AsyncRelayCommand(() => ExportXlsxAsync?.Invoke() ?? Task.CompletedTask, _ => Entries.Any());
        ClearCacheCommand       = new RelayCommand(_ => ClearCache());
        SaveColumnLayoutCommand = new RelayCommand(_ => SaveColumnLayout?.Invoke());
        BrowseFolderCommand     = new RelayCommand(_ => BrowseFolder?.Invoke());
        AddExtraFolderCommand   = new RelayCommand(_ => AddExtraFolder());
        CloseTabCommand         = new RelayCommand(p => CloseTab(p as LibraryTab));
        RowDoubleClickCommand   = new AsyncRelayCommand<LibraryEntry>(e => { if (e != null) { LoadEntryIntoPanel?.Invoke(e); FileDoubleClicked?.Invoke(e); } return Task.CompletedTask; });
        LoadSelectedCommand     = new RelayCommand(p => { if (p is System.Collections.IList list) LoadSelectionIntoPanel?.Invoke(list.Cast<object>().ToList()); });
    }

    private bool PassesFilter(LibraryEntry e)
    {
        if (_settings.LibraryTabbedMode && _selectedTab != null)
        {
            if (!e.FilePath.StartsWith(_selectedTab.FolderPath, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        if (string.IsNullOrWhiteSpace(_filterText)) return true;
        var f = _filterText.ToLowerInvariant();
        return (e.Title?.ToLowerInvariant().Contains(f) == true)
            || (e.Genre?.ToLowerInvariant().Contains(f) == true)
            || (e.Director?.ToLowerInvariant().Contains(f) == true)
            || (e.Cast?.ToLowerInvariant().Contains(f) == true)
            || (e.Year?.Contains(f) == true)
            || (e.ShowTitle?.ToLowerInvariant().Contains(f) == true);
    }

    private void ClearCache() { LibraryCacheService.ClearAll(); CacheInfo = "Cache cleared."; }

    private void AddExtraFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Add extra library folder", Multiselect = false };
        if (dlg.ShowDialog() != true) return;
        if (!ExtraFolders.Contains(dlg.FolderName))
        {
            ExtraFolders.Add(dlg.FolderName);
            _settings.ExtraLibraryFolders = ExtraFolders.ToList();
            _ = App.ConfigService.SaveAsync();
            if (_settings.LibraryWatchEnabled) ApplyWatchSetting();
        }
    }

    private void CloseTab(LibraryTab? tab)
    {
        if (tab == null) return;
        Tabs.Remove(tab);
        if (!tab.FolderPath.Equals(_settings.LibraryFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            ExtraFolders.Remove(tab.FolderPath);
            _settings.ExtraLibraryFolders.Remove(tab.FolderPath);
            _ = App.ConfigService.SaveAsync();
        }
        if (_settings.LibraryWatchEnabled) ApplyWatchSetting();
    }

    public void ApplyWatchSetting()
    {
        _watchService.Stop();
        if (!_settings.LibraryWatchEnabled) return;
        var all = new List<string>();
        if (!string.IsNullOrWhiteSpace(_settings.LibraryFolderPath)) all.Add(_settings.LibraryFolderPath);
        all.AddRange(ExtraFolders.Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f)));
        if (all.Count == 0) return;
        _watchService.Start(all, Entries.Select(e => e.FilePath), _settings.LibraryWatchPollMinutes);
    }
}
