using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;

namespace VideoMetadataEditor.ViewModels;

// ── Event Args ────────────────────────────────────────────────────────────────

public class RenameEventArgs(string oldName, string newName, string error = "") : EventArgs
{
    public string OldName { get; } = oldName;
    public string NewName { get; } = newName;
    public string Error   { get; } = error;
}

public class TransferEventArgs : EventArgs
{
    public bool   IsMove      { get; init; }
    public int    Succeeded   { get; init; }
    public int    Failed      { get; init; }
    public int    Verified    { get; init; }
    public long   BytesMoved  { get; init; }
    public string Destination { get; init; } = string.Empty;
    public bool   HasErrors   => Failed > 0;
}

public class EmbedEventArgs(string fileName, bool wasRenamed) : EventArgs
{
    public string FileName   { get; } = fileName;
    public bool   WasRenamed { get; } = wasRenamed;
}

public class BatchEventArgs(int successCount, int failCount, int renamedCount) : EventArgs
{
    public int SuccessCount { get; } = successCount;
    public int FailCount    { get; } = failCount;
    public int RenamedCount { get; } = renamedCount;
}

public class FilesLoadedEventArgs(int added, int total, string folderPath) : EventArgs
{
    public int    Added      { get; } = added;
    public int    Total      { get; } = total;
    public string FolderPath { get; } = folderPath;
    public string FolderName => System.IO.Path.GetFileName(FolderPath.TrimEnd('\\', '/'));
}

public class DeleteFileEventArgs(string filePath, string fileName) : EventArgs
{
    public string FilePath { get; } = filePath;
    public string FileName { get; } = fileName;
}

// ── ViewModel ─────────────────────────────────────────────────────────────────

public partial class MainViewModel : INotifyPropertyChanged
{
    // ── Services ──────────────────────────────────────────────────────────────

    private readonly MetadataService    _metadataService = new();
    private readonly ApiService         _apiService      = new();
    private readonly Services.AniListApiService _aniListService  = new();
    private readonly FileRenameService  _renameService   = new();
    private readonly LibraryScanService _libraryScanService;
    private readonly Services.LibraryCacheService _libraryCacheService;

    // ── Phase 2: Dedicated sub-ViewModels ─────────────────────────────────────
    /// <summary>Owns copy/move engine, destination, conflict mode, progress.</summary>
    public CopyMoveViewModel Transfer { get; private set; } = null!;
    /// <summary>Owns search, retrieval, TV episode picker.</summary>
    public SearchViewModel   Search   { get; private set; } = null!;
    /// <summary>Owns metadata editing, write status, artwork, rename, batch.</summary>
    public MetadataViewModel Metadata { get; private set; } = null!;

    // ── Duplicates ───────────────────────────────────────────────────────────
    public DuplicatesViewModel DuplicatesVM { get; }
    public MediaHealthViewModel MediaHealthVM { get; } = new(new Services.WpfDialogService(), new Services.WpfClipboardService());

    // ── Watch Folder (Files panel) ───────────────────────────────────────────
    private readonly Services.WatchFolderService _watchFolderService = new();

    // ── Library Watch ─────────────────────────────────────────────────────────
    private readonly Services.MultiWatchFolderService _libraryWatchService = new();

    // ── Library Tab ───────────────────────────────────────────────────────────
    public ObservableCollection<Models.LibraryEntry> LibraryEntries { get; } = new();

    // ── Library filter ────────────────────────────────────────────────────────
    private System.ComponentModel.ICollectionView? _libraryView;
    /// <summary>Filtered view of LibraryEntries — bound to the DataGrid.</summary>
    public System.ComponentModel.ICollectionView LibraryView =>
        _libraryView ??= InitLibraryView();

    private System.ComponentModel.ICollectionView InitLibraryView()
    {
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(LibraryEntries);
        view.Filter = obj => obj is Models.LibraryEntry e && MatchesLibraryFilter(e);
        return view;
    }

    private string _libraryFilterText = string.Empty;
    public string LibraryFilterText
    {
        get => _libraryFilterText;
        set
        {
            Set(ref _libraryFilterText, value);
            LibraryView.Refresh();
            RaiseProperty(nameof(LibraryFilterCount));
        }
    }

    public string LibraryFilterCount
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_libraryFilterText)) return string.Empty;
            var visible = LibraryEntries.Count(e => MatchesLibraryFilter(e));
            return $"{visible} / {LibraryEntries.Count}";
        }
    }

    private bool MatchesLibraryFilter(Models.LibraryEntry e)
    {
        // Tab filter — only apply when tabbed mode is on and a tab is selected
        if (Settings.LibraryTabbedMode && _selectedLibraryTab != null)
        {
            if (!e.FilePath.StartsWith(_selectedLibraryTab.FolderPath,
                StringComparison.OrdinalIgnoreCase))
                return false;
        }

        var f = _libraryFilterText.Trim();
        if (string.IsNullOrEmpty(f)) return true;
        return e.Title.Contains(f, StringComparison.OrdinalIgnoreCase)
            || e.Year.Contains(f, StringComparison.OrdinalIgnoreCase)
            || e.Genre.Contains(f, StringComparison.OrdinalIgnoreCase)
            || e.Director.Contains(f, StringComparison.OrdinalIgnoreCase)
            || e.Cast.Contains(f, StringComparison.OrdinalIgnoreCase)
            || e.ImdbId.Contains(f, StringComparison.OrdinalIgnoreCase)
            || e.MpaRating.Contains(f, StringComparison.OrdinalIgnoreCase)
            || e.ShowTitle.Contains(f, StringComparison.OrdinalIgnoreCase);
    }
    public ObservableCollection<Models.LibraryColumn> LibraryColumns { get; } = new();

    private bool _isLibraryScanning;
    public bool IsLibraryScanning
    {
        get => _isLibraryScanning;
        set { Set(ref _isLibraryScanning, value); RaiseProperty(nameof(CanScanLibrary)); }
    }

    private int _libraryScanProgress;
    public int LibraryScanProgress
    {
        get => _libraryScanProgress;
        set => Set(ref _libraryScanProgress, value);
    }

    private string _libraryScanStatus = string.Empty;
    public string LibraryScanStatus
    {
        get => _libraryScanStatus;
        set => Set(ref _libraryScanStatus, value);
    }

    public bool CanScanLibrary => !IsLibraryScanning && !IsBusy &&
                                   !string.IsNullOrWhiteSpace(Settings.LibraryFolderPath);

    // ── Extra library folders (multi-folder support) ──────────────────────────
    public System.Collections.ObjectModel.ObservableCollection<string> ExtraLibraryFolders { get; }
        = new();

    public ICommand AddExtraLibraryFolderCommand    { get; private set; } = null!;
    public ICommand RemoveExtraLibraryFolderCommand { get; private set; } = null!;

    // ── Tabbed Library ────────────────────────────────────────────────────────
    public System.Collections.ObjectModel.ObservableCollection<Models.LibraryTab> LibraryTabs { get; } = new();

    private Models.LibraryTab? _selectedLibraryTab;
    public Models.LibraryTab? SelectedLibraryTab
    {
        get => _selectedLibraryTab;
        set
        {
            Set(ref _selectedLibraryTab, value);
            RaiseProperty(nameof(HasMultipleTabs));
            RaiseProperty(nameof(ActiveTabIsTv));
            RaiseProperty(nameof(LibraryShowTreeView));
            RaiseProperty(nameof(LibraryShowDataGrid));
            // Reset filter text to selected tab's saved filter, refresh view
            if (value != null)
            {
                _libraryFilterText = value.FilterText;
                RaiseProperty(nameof(LibraryFilterText));
            }
            LibraryView?.Refresh();
            RaiseProperty(nameof(LibraryFilterCount));
            // Re-apply column visibility based on tab type
            ApplyTabColumns();
        }
    }

    /// <summary>
    /// True when the active library tab is a TV Shows folder.
    /// Used by the DataGrid to show/hide TV-specific columns.
    /// Heuristic: folder path contains "TV" or "Shows" or the tab's entries
    /// are predominantly episodes.
    /// </summary>
    public bool ActiveTabIsTv
    {
        get
        {
            if (_selectedLibraryTab == null) return false;

            // ── User explicit override (set via context menu on the tab) ───────
            if (_selectedLibraryTab.IsTvOverride.HasValue)
                return _selectedLibraryTab.IsTvOverride.Value;

            var path = _selectedLibraryTab.FolderPath.ToUpperInvariant();

            // ── Path-based detection (most reliable — user folder names are intentional)
            if (path.Contains("TV") || path.Contains("SHOW") || path.Contains("SERIE")
                || path.Contains("ANIME") || path.Contains("EPISODE"))
                return true;

            // ── Explicit non-TV folder names
            if (path.Contains("MOVIE") || path.Contains("FILM") || path.Contains("CINEMA"))
                return false;

            // ── Majority vote — use this tab's entries only
            var entries = _selectedLibraryTab.Entries;
            if (entries.Count > 0)
                return entries.Count(e => e.IsEpisode) > entries.Count / 2;

            return false;
        }
    }

    /// <summary>Toggle the TV/Movie type override for the currently selected tab.</summary>
    public void ToggleCurrentTabType()
    {
        if (_selectedLibraryTab == null) return;
        var newVal = !ActiveTabIsTv;
        _selectedLibraryTab.IsTvOverride = newVal;
        // Persist to Settings
        if (!string.IsNullOrWhiteSpace(_selectedLibraryTab.FolderPath))
            Settings.TabTypeOverrides[_selectedLibraryTab.FolderPath] = newVal;
        _ = App.ConfigService.SaveAsync();
        RaiseProperty(nameof(ActiveTabIsTv));
        RaiseProperty(nameof(LibraryShowDataGrid));
        RaiseProperty(nameof(LibraryShowTreeView));
        ApplyTabColumns();
    }

    /// <summary>
    /// Applies column visibility based on the active tab type.
    /// Movie tabs hide TV-specific columns; TV tabs show them and hide movie-only columns.
    /// </summary>
    private void ApplyTabColumns()
    {
        bool isTv     = ActiveTabIsTv;
        var baseCols  = isTv ? _tvColumns : _movieColumns;
        var savedCols = isTv ? Settings.TvColumns : Settings.MovieColumns;
        var baseIds   = baseCols.Select(c => c.Id).ToHashSet();
        bool hasSaved = savedCols.Count > 0;

        // ── Step 1: Set visibility and width on every LibraryColumn ───────────
        foreach (var col in LibraryColumns)
        {
            var saved = hasSaved ? savedCols.FirstOrDefault(c => c.Id == col.Id) : default;
            var def   = baseCols.FirstOrDefault(c => c.Id == col.Id);

            col.IsVisible = hasSaved
                ? (saved?.IsVisible ?? baseIds.Contains(col.Id))
                : baseIds.Contains(col.Id);

            if (def != default) col.Header = def.Header;
            if (saved != null && saved.Width > 10) col.Width = saved.Width;
        }

        // ── Step 2: Assign DisplayIndex in strict 0-N order ───────────────────
        // Strategy: visible columns sorted by saved/base order get 0, 1, 2...
        // Hidden columns get the trailing indices.
        // ALL columns must be assigned to prevent WPF from leaving stale indices
        // that conflict when the tab is switched (this is what moves Cover to the end).

        var orderedVisible = hasSaved
            ? savedCols
                .Where(s => LibraryColumns.Any(c => c.Id == s.Id && c.IsVisible))
                .OrderBy(s => s.DisplayIndex)
                .Select(s => LibraryColumns.First(c => c.Id == s.Id))
                .ToList()
            : baseCols
                .Select(b => LibraryColumns.FirstOrDefault(c => c.Id == b.Id))
                .Where(c => c != null && c.IsVisible)
                .ToList()!;

        // Add any visible columns not in the saved/base set (e.g. newly added columns)
        var extraVisible = LibraryColumns
            .Where(c => c.IsVisible && !orderedVisible.Contains(c))
            .OrderBy(c => c.DisplayIndex)
            .ToList();
        orderedVisible.AddRange(extraVisible);

        var hidden = LibraryColumns
            .Where(c => !c.IsVisible)
            .OrderBy(c => c.DisplayIndex)
            .ToList();

        int di = 0;
        foreach (var col in orderedVisible) col.DisplayIndex = di++;
        foreach (var col in hidden)         col.DisplayIndex = di++;

        // ── Step 3: Notify tree/grid visibility ───────────────────────────────
        RaiseProperty(nameof(TvShowTree));
        RaiseProperty(nameof(LibraryShowDataGrid));
        RaiseProperty(nameof(LibraryShowTreeView));

        // ── Step 4: Sync to DataGrid on UI thread ─────────────────────────────
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (System.Windows.Application.Current?.MainWindow is Views.MainWindow mw)
                mw.SyncLibraryColumnVisibility();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// True only when TabbedMode is ON and there is at least one tab built.
    /// The Settings toggle is the sole gate — with 2 folders unchecking hides tabs
    /// and shows the unified merged view.
    /// </summary>
    public bool HasMultipleTabs => Settings.LibraryTabbedMode && LibraryTabs.Count >= 1;

    private bool _libraryTabbedMode => Settings.LibraryTabbedMode;

    public ICommand CloseLibraryTabCommand        { get; private set; } = null!;
    public ICommand SetTabAsMoviesCommand         { get; private set; } = null!;
    public ICommand SetTabAsTvCommand             { get; private set; } = null!;
    public ICommand ResetTabTypeCommand           { get; private set; } = null!;
    public ICommand SaveLibraryColumnLayoutCommand { get; private set; } = null!;
    /// <summary>Set by MainWindow code-behind to sync DataGrid widths before save.</summary>
    public Action? SyncLibraryGridBeforeSave { get; set; }

    /// <summary>
    /// Checks GitHub releases for a newer version and surfaces a notification
    /// banner if one is available. Fires once per session on startup.
    /// Non-fatal — any failure is silently swallowed.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        try
        {
            const string api = "https://api.github.com/repos/severdthumbz-coder/VideoMetadataEditor/releases/latest";
            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "VideoMetadataEditor-UpdateCheck");
            http.Timeout = TimeSpan.FromSeconds(6);

            var json = await http.GetStringAsync(api).ConfigureAwait(false);
            // Parse "tag_name": "v1.4.0.91" cheaply without a full JSON dependency
            var tagMatch = System.Text.RegularExpressions.Regex.Match(json, @"""tag_name""\s*:\s*""([^""]+)""");
            if (!tagMatch.Success) return;

            var remoteTag  = tagMatch.Groups[1].Value.TrimStart('v');
            var localVer   = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (localVer == null) return;
            if (!Version.TryParse(remoteTag, out var remoteVer)) return;
            if (remoteVer <= localVer) return;

            // Newer version available — show a non-intrusive banner
            var urlMatch = System.Text.RegularExpressions.Regex.Match(json, @"""html_url""\s*:\s*""([^""]+)""");
            string releaseUrl = urlMatch.Success ? urlMatch.Groups[1].Value : "https://github.com/severdthumbz-coder/VideoMetadataEditor/releases";

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                StatusText = $"🆕 Update available: v{remoteVer} — see Help → Changelog or visit GitHub";
                Log($"[{DateTime.Now:HH:mm:ss}] Update available: v{remoteVer} (current: v{localVer})");
            });
        }
        catch { /* non-fatal — no internet, private repo, etc */ }
    }

    /// <summary>
    /// Synchronously saves the library cache on app close so embeds made
    /// this session survive a restart. Called from MainWindow_Closing.
    /// </summary>
    public void FlushLibraryCacheSync(IReadOnlyList<string> livePaths)
    {
        // SaveAsync is async — run it synchronously via GetAwaiter().GetResult()
        // only safe here because we're in the Closing handler (UI thread shutting down).
        _libraryCacheService.SaveAsync(livePaths).GetAwaiter().GetResult();
    }

    /// <summary>Disposes FileSystemWatcher resources on app close to release OS handles.</summary>
    public void DisposeWatchServices()
    {
        try { _watchFolderService.Dispose(); }   catch { }
        try { _libraryWatchService.Dispose(); }  catch { }
    }

    public ICommand SelectLibraryTabCommand { get; private set; } = null!;


    // ── Notification Events (consumed by MainWindow to show toasts) ───────────

    public event EventHandler<RenameEventArgs>?    RenameSucceeded;
    public event EventHandler<RenameEventArgs>?    RenameFailed;
    public event EventHandler<TransferEventArgs>?  CopyCompleted;
    public event EventHandler<TransferEventArgs>?  MoveCompleted;
    public event EventHandler<EmbedEventArgs>?  EmbedSucceeded;
    public event EventHandler<BatchEventArgs>?  BatchCompleted;

    // ── Batch undo state ──────────────────────────────────────────────────────
    // Stores the files that succeeded in the last batch so they can all be
    // reverted together. Cleared at the start of every new batch run.
    private readonly List<VideoFile> _batchUndoFiles = new();
    public bool CanUndoBatch => _batchUndoFiles.Count > 0 && !IsBusy;
    public ICommand UndoBatchCommand { get; }
    public event EventHandler<FilesLoadedEventArgs>? FilesLoaded;
    /// <summary>Fires when a write fails AND VerboseWriteErrors is enabled. Arg: (file, detail message).</summary>
    public event EventHandler<(VideoFile File, string Detail)>? WriteFailedDetailed;

    /// <summary>
    /// Fires when orphan VME temp/backup files are detected after a folder/library load.
    /// Code-behind opens the RecoveryDialog in response.
    /// </summary>
    public event EventHandler<IReadOnlyList<Services.RecoveryCandidate>>? RecoveryCandidatesFound;
    public event EventHandler<DeleteFileEventArgs>? DeleteFileRequested;
    public event EventHandler<VideoFile>? ReadOnlyFileBlocked;

    // ── File Collection ───────────────────────────────────────────────────────

    public ObservableCollection<VideoFile> Files { get; } = [];

    // ── Constructor ───────────────────────────────────────────────────────────

    // Hook selection changes so SelectedFileCount and CanCopyMove stay current
    private void HookFilesCollection()
    {
        Files.CollectionChanged += (_, e) =>
        {
            RaiseProperty(nameof(SelectedFileCount));
            RaiseProperty(nameof(CanCopyMove));

            // Also hook any newly added files' property changes
            if (e.NewItems == null) return;
            foreach (VideoFile vf in e.NewItems)
                vf.PropertyChanged += (__, pe) =>
                {
                    if (pe.PropertyName == nameof(VideoFile.IsSelected))
                    {
                        RaiseProperty(nameof(SelectedFileCount));
                        RaiseProperty(nameof(CanCopyMove));
                    }
                };
        };
    }

    private VideoFile? _selectedFile;
    public VideoFile? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (_selectedFile == value) return;
            CachePendingChanges();
            Set(ref _selectedFile, value);
            OnSelectedFileChanged();
        }
    }

    // ── Metadata Editing (bound to Raw Data tab) ──────────────────────────────

    public MovieMetadata EditingMetadata { get; } = new();

    // ── Search / Retrieved ────────────────────────────────────────────────────

    private string _searchQuery = string.Empty;
    public string SearchQuery { get => _searchQuery; set => Set(ref _searchQuery, value); }

    private string _idLookup = string.Empty;
    public string IdLookup { get => _idLookup; set => Set(ref _idLookup, value); }

    public ObservableCollection<SearchResult> SearchResults { get; } = [];

    // Pagination — stores all results, SearchResults shows the current page
    private List<SearchResult> _allSearchResults = [];
    private int _searchPage = 0;
    private const int PageSize = 20;

    private bool _hasMoreResults;
    public bool HasMoreResults { get => _hasMoreResults; set => Set(ref _hasMoreResults, value); }

    private SearchResult? _selectedSearchResult;
    public SearchResult? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set { Set(ref _selectedSearchResult, value); }
    }

    // ── Retrieved Metadata display ────────────────────────────────────────────

    private MovieMetadata? _retrievedMetadata;
    public MovieMetadata? RetrievedMetadata
    {
        get => _retrievedMetadata;
        set { Set(ref _retrievedMetadata, value); RaiseProperty(nameof(HasTvEpisodeResult)); }
    }

    // ── Settings auto-save ────────────────────────────────────────────────────

    private System.Windows.Threading.DispatcherTimer? _settingsSaveTimer;

    /// <summary>Debounced auto-save — coalesces rapid UI changes into one write.</summary>
    public void ScheduleSettingsSave()
    {
        if (!Settings.AutoSaveSettings) return;
        _settingsSaveTimer?.Stop();
        _settingsSaveTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(800)
        };
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            _ = App.ConfigService.SaveAsync();
        };
        _settingsSaveTimer.Start();
    }

    // ── Status / Progress ─────────────────────────────────────────────────────

    private string _statusText = "Ready";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            Set(ref _isBusy, value);
            RaiseProperty(nameof(IsProgressVisible));
        }
    }

    /// <summary>True when at least one file in the Files panel has a write error.</summary>
    public bool HasAnyFailure => Files.Any(f => !f.IsSeparator && f.HasError);

    private int _progressValue;
    public int ProgressValue
    {
        get => _progressValue;
        set
        {
            Set(ref _progressValue, value);
            RaiseProperty(nameof(IsProgressVisible));
        }
    }

    // Show progress bar whenever there's active work or live progress
    public bool IsProgressVisible => IsBusy || ProgressValue > 0;

    public ObservableCollection<string> ConsoleLog { get; } = [];

    /// <summary>
    /// Thread-safe ConsoleLog insert. Safe to call from any thread — dispatches
    /// to the UI thread automatically if needed. Replaces direct Log(...)
    /// calls from background tasks which throw NotSupportedException on WPF collection views.
    /// </summary>
    private void Log(string message)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            ConsoleLog.Insert(0, message);
        else
            dispatcher.InvokeAsync(() => ConsoleLog.Insert(0, message),
                System.Windows.Threading.DispatcherPriority.Background);
    }

    // ── Copy / Move Tab ───────────────────────────────────────────────────────

    private string _copyDestination = string.Empty;
    public string CopyDestination
    {
        get => _copyDestination;
        set
        {
            Set(ref _copyDestination, value);
            RaiseProperty(nameof(CanCopyMove));
            // Persist to config so it survives app restart
            Settings.LastCopyDestination = value;
            _ = App.ConfigService.SaveAsync();
            RaiseProperty(nameof(SavedCopyDestination));
        }
    }

    public bool CanCopyMove =>
        !IsBusy &&
        Files.Any(f => f.IsSelected && !f.IsSeparator) &&
        !string.IsNullOrWhiteSpace(CopyDestination);

    public int SelectedFileCount => Files.Count(f => f.IsSelected && !f.IsSeparator);

    private string _diskSpaceStatus = string.Empty;
    public string DiskSpaceStatus { get => _diskSpaceStatus; set => Set(ref _diskSpaceStatus, value); }

    // ── Space Analysis panel ──────────────────────────────────────────────────

    private bool _spaceAnalysisVisible;
    public bool SpaceAnalysisVisible { get => _spaceAnalysisVisible; set => Set(ref _spaceAnalysisVisible, value); }

    private string _spaceSourceInfo   = string.Empty;
    private string _spaceDestInfo     = string.Empty;
    private string _spaceDiskUsage    = string.Empty;
    private string _spaceStatusMsg    = string.Empty;
    private bool   _spaceIsSufficient;

    public string SpaceSourceInfo  { get => _spaceSourceInfo;  set => Set(ref _spaceSourceInfo,  value); }
    public string SpaceDestInfo    { get => _spaceDestInfo;    set => Set(ref _spaceDestInfo,    value); }
    public string SpaceDiskUsage   { get => _spaceDiskUsage;   set => Set(ref _spaceDiskUsage,   value); }
    public string SpaceStatusMsg   { get => _spaceStatusMsg;   set => Set(ref _spaceStatusMsg,   value); }
    public bool   SpaceIsSufficient { get => _spaceIsSufficient; set => Set(ref _spaceIsSufficient, value); }

    // ── File Conflict Resolution ──────────────────────────────────────────────

    // Options: Skip | Overwrite | OverwriteIfNewer | Rename
    private string _conflictMode = "Skip";
    public string ConflictMode
    {
        get => _conflictMode;
        set
        {
            Set(ref _conflictMode, value);
            RaiseProperty(nameof(ConflictModeDescription));
            // Persist to config so it survives app restart
            Settings.LastConflictMode = value;
            _ = App.ConfigService.SaveAsync();
            RaiseProperty(nameof(SavedConflictMode));
        }
    }

    public string ConflictModeDescription => ConflictMode switch
    {
        "Skip"             => "Skips the file if it already exists in the destination. Source file is left untouched.",
        "Overwrite"        => "Replaces any existing file in the destination with the source file.",
        "OverwriteIfNewer" => "Replaces the existing file only if the source file is newer (based on modified date).",
        "Rename"           => "Keeps both files by appending a number to the source file name, e.g. Movie (2).mp4.",
        _                  => string.Empty
    };

    public bool FastCopyDetected => Services.FileCopyService.FastCopyAvailable;

    /// <summary>
    /// Live description of the recommended write worker count for the currently
    /// loaded files — shown in the Settings Performance group.
    /// </summary>
    public string WriteWorkerRecommendation
    {
        get
        {
            var sample = Files.FirstOrDefault(f => !string.IsNullOrEmpty(f.FilePath))?.FilePath
                         ?? Settings.LastFolderPath;
            if (string.IsNullOrEmpty(sample))
                return $"Recommended: {Math.Min(Environment.ProcessorCount, 4)} workers (no files loaded yet)";
            try { return $"Recommended: {Services.WriteThreadService.Describe(sample)}"; }
            catch { return "Recommended: 4 workers (default)"; }
        }
    }

    public string DetectedEngineText => FastCopyDetected
        ? "FastCopy ✓ detected  —  Auto mode will use FastCopy"
        : "FastCopy not found  —  Custom Fast (Recommended) will be used";

    // ── Tab Index ─────────────────────────────────────────────────────────────

    private int _selectedTabIndex;
    public int SelectedTabIndex { get => _selectedTabIndex; set => Set(ref _selectedTabIndex, value); }

    // ── Artwork ───────────────────────────────────────────────────────────────

    private BitmapImage? _artworkImage;
    public BitmapImage? ArtworkImage { get => _artworkImage; set => Set(ref _artworkImage, value); }

    // ── Media Info ────────────────────────────────────────────────────────────

    private VideoMetadataEditor.Services.VideoMediaInfo? _mediaInfo;
    public VideoMetadataEditor.Services.VideoMediaInfo? MediaInfo
    {
        get => _mediaInfo;
        set => Set(ref _mediaInfo, value);
    }

    private bool _isPreviewPlaying;
    public bool IsPreviewPlaying { get => _isPreviewPlaying; set => Set(ref _isPreviewPlaying, value); }

    // ── Cancellation ─────────────────────────────────────────────────────────

    private CancellationTokenSource? _cts;

    private bool _isCancelling;
    public bool IsCancelling
    {
        get => _isCancelling;
        set { Set(ref _isCancelling, value); RaiseProperty(nameof(CancelButtonLabel)); }
    }
    public string CancelButtonLabel => IsCancelling ? "Cancelling…" : "✕ Cancel";

    private void RequestCancel()
    {
        _cts?.Cancel();
        _libraryCts?.Cancel();  // also cancel any in-progress library scan
        IsCancelling = true;
        StatusText = "Cancelling… (completing current file safely)";
        Log( $"[{DateTime.Now:HH:mm:ss}] ✕ Cancel requested — waiting for safe stop point.");
    }

    private CancellationToken BeginOperation()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        IsCancelling = false;
        IsBusy = false;   // ensure clean slate if previous op leaked
        return _cts.Token;
    }

    /// <summary>
    /// Called in every finally block. Resets IsBusy, IsCancelling, and
    /// ProgressValue so the UI always returns to a clean idle state regardless
    /// of how the operation ended (success, error, or cancellation).
    /// </summary>
    private void EndOperation(bool resetProgress = true)
    {
        IsBusy      = false;
        IsCancelling = false;
        if (resetProgress) ProgressValue = 0;
    }

    // ── Auto-search debounce ──────────────────────────────────────────────────

    private System.Windows.Threading.DispatcherTimer? _searchDebounce;

    private void ScheduleAutoSearch(VideoFile targetFile, string title, string year)
    {
        _searchDebounce?.Stop();
        _searchDebounce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(Math.Max(0, Settings.AutoSearchDebounceMs))
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _ = AutoSearchAsync(targetFile, title, year);
        };
        _searchDebounce.Start();
    }

    // ── Duplicates ────────────────────────────────────────────────────────────

    private ObservableCollection<Services.DuplicateGroup> _duplicates = [];
    public ObservableCollection<Services.DuplicateGroup> Duplicates
    {
        get => _duplicates;
        set => Set(ref _duplicates, value);
    }

    private bool _showDuplicatesOnly;
    public bool ShowDuplicatesOnly
    {
        get => _showDuplicatesOnly;
        set { Set(ref _showDuplicatesOnly, value); }
    }

    // ── Rename Presets ────────────────────────────────────────────────────────

    public ObservableCollection<string> RenamePresetList =>
        new(Settings.RenamePresets);

    // ── Format Capabilities ───────────────────────────────────────────────────

    private bool _formatSupportsArtwork = true;
    public bool FormatSupportsArtwork { get => _formatSupportsArtwork; set => Set(ref _formatSupportsArtwork, value); }

    private bool _formatSupportsFullTags = true;
    public bool FormatSupportsFullTags { get => _formatSupportsFullTags; set => Set(ref _formatSupportsFullTags, value); }

    private string? _formatWarning;
    public string? FormatWarning { get => _formatWarning; set => Set(ref _formatWarning, value); }

    // ── Rename Preview ────────────────────────────────────────────────────────

    private string _renamePreview = string.Empty;
    public string RenamePreview { get => _renamePreview; set => Set(ref _renamePreview, value); }

    // ── Parsed filename info (shown in Raw Data tab) ──────────────────────────

    private string? _parsedFileInfo;
    public string? ParsedFileInfo { get => _parsedFileInfo; set => Set(ref _parsedFileInfo, value); }

    // ── ID Refinement hint (shown in Retrieved Data tab when IDs are present) ─

    public bool HasIdRefinement =>
        !string.IsNullOrWhiteSpace(EditingMetadata.ImdbId) ||
        !string.IsNullOrWhiteSpace(EditingMetadata.TmdbId);

    public string IdRefinementHint
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(EditingMetadata.ImdbId))
                parts.Add($"IMDB: {EditingMetadata.ImdbId}");
            if (!string.IsNullOrWhiteSpace(EditingMetadata.TmdbId))
                parts.Add($"TMDB: {EditingMetadata.TmdbId}");
            return string.Join("  ·  ", parts);
        }
    }

    // ── App Version ───────────────────────────────────────────────────────────

    public string AppVersion     { get; } = BuildVersionString();
    public string AppVersionFull { get; } = BuildVersionStringFull();

    private static string BuildVersionString()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.0";
    }

    private static string BuildVersionStringFull()
    {
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return v != null
            ? $"v{v.Major}.{v.Minor}.{v.Build} build {v.Revision}"
            : "v1.0.0 build 1";
    }

    // ── Original Filename (read-only display in Raw Data tab) ─────────────────

    private string _originalFileName = string.Empty;
    public string OriginalFileName { get => _originalFileName; set => Set(ref _originalFileName, value); }

    // ── Settings (proxy) ─────────────────────────────────────────────────────

    public AppSettings Settings => App.ConfigService.Settings;

    // ── TV Mode ───────────────────────────────────────────────────────────────
    /// <summary>
    /// True when the current file is being treated as a TV episode.
    /// Syncs with EditingMetadata.IsEpisode and drives UI panel visibility.
    /// </summary>
    public bool IsEpisodeMode
    {
        get => EditingMetadata?.IsEpisode ?? false;
        set
        {
            if (EditingMetadata == null) return;
            if (EditingMetadata.IsEpisode == value) return; // no change — no side effects
            EditingMetadata.IsEpisode = value;
            RaiseProperty();
            RaiseProperty(nameof(IsMovieMode));
            // Clear previous search result and picker only when user explicitly toggles
            // Do NOT clear _selectedSeriesTmdbId — PickTvEpisodeAsync needs it to survive
            // the ApplyRetrievedToEditing() call that triggers this setter
            RetrievedMetadata = null;
            IsTvPickerVisible = false;
            TvSeasonNumbers.Clear();
            TvEpisodeItems.Clear();
            TvSelectedEpisode = null;
            // NOTE: _selectedSeriesTmdbId intentionally NOT cleared here —
            // it must survive through PickTvEpisodeAsync's call chain
            if (value && !Settings.RenamePattern.Contains("{Episode}"))
                RaiseProperty(nameof(TvRenamePatternSuggestion));
        }
    }
    public bool IsMovieMode => !IsEpisodeMode;

    /// <summary>True when a TV episode has been loaded via the picker and is ready to apply.</summary>
    public bool HasTvEpisodeResult =>
        RetrievedMetadata?.IsEpisode == true && !IsTvPickerVisible;

    /// <summary>Default TV rename pattern suggestion shown in tooltip.</summary>
    public string TvRenamePatternSuggestion =>
        "{ShowTitle} - S{Season}E{Episode} - {EpisodeTitle}";

    // ── TV picker state ───────────────────────────────────────────────────────────
    private string _selectedSeriesTmdbId = string.Empty;
    private CancellationTokenSource? _tvPickerCts;  // cancels in-flight ShowTvPickerAsync

    private string _tvPickerShowTitle = string.Empty;
    public string TvPickerShowTitle
    {
        get => _tvPickerShowTitle;
        set => Set(ref _tvPickerShowTitle, value);
    }

    private bool _isTvPickerVisible;
    public bool IsTvPickerVisible
    {
        get => _isTvPickerVisible;
        set
        {
            Set(ref _isTvPickerVisible, value);
            RaiseProperty(nameof(HasTvEpisodeResult));
        }
    }

    // Season list (1..N integers) bound to season ComboBox
    public System.Collections.ObjectModel.ObservableCollection<int> TvSeasonNumbers { get; } = new();

    private int _tvSelectedSeason = 1;
    private bool _suppressSeasonAutoLoad = false; // set to true during programmatic init

    public int TvSelectedSeason
    {
        get => _tvSelectedSeason;
        set
        {
            Set(ref _tvSelectedSeason, value);
            // Only fire-and-forget when user manually changes season (combobox)
            // Programmatic changes during ShowTvPickerAsync set _suppressSeasonAutoLoad=true
            if (value > 0 && !_suppressSeasonAutoLoad)
                _ = LoadTvSeasonEpisodesAsync(value);
        }
    }

    // Episode list for the selected season
    public System.Collections.ObjectModel.ObservableCollection<Models.TvEpisodeItem> TvEpisodeItems { get; } = new();

    private Models.TvEpisodeItem? _tvSelectedEpisode;
    public Models.TvEpisodeItem? TvSelectedEpisode
    {
        get => _tvSelectedEpisode;
        set
        {
            Set(ref _tvSelectedEpisode, value);
            RaiseProperty(nameof(CanPickTvEpisode));
            // Force the command to re-evaluate its CanExecute state
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Normal,
                new Action(CommandManager.InvalidateRequerySuggested));
        }
    }

    public bool CanPickTvEpisode => _tvSelectedEpisode != null && !string.IsNullOrEmpty(_selectedSeriesTmdbId);

    private string _directEpisodeEntry = string.Empty;
    public string DirectEpisodeEntry
    {
        get => _directEpisodeEntry;
        set => Set(ref _directEpisodeEntry, value);
    }

    /// <summary>
    /// Parses typed "S01E02" or "2" and selects that episode in the current season.
    /// Called when user presses Enter in the DirectEpisodeBox.
    /// </summary>
    public void ApplyDirectEpisodeEntry()
    {
        var entry = DirectEpisodeEntry.Trim();
        if (string.IsNullOrWhiteSpace(entry)) return;

        int? targetSeason  = null;
        int? targetEpisode = null;

        // Pattern: S01E02 or s1e2
        var m = System.Text.RegularExpressions.Regex.Match(entry,
            @"(?i)[Ss](\d{1,2})[Ee](\d{1,3})");
        if (m.Success)
        {
            targetSeason  = int.Parse(m.Groups[1].Value);
            targetEpisode = int.Parse(m.Groups[2].Value);
        }
        else if (int.TryParse(entry, out var epNum) && epNum > 0)
        {
            // Just a number — use current season
            targetEpisode = epNum;
        }

        if (targetSeason.HasValue && targetSeason.Value != TvSelectedSeason)
            TvSelectedSeason = targetSeason.Value;

        if (targetEpisode.HasValue)
        {
            var ep = TvEpisodeItems.FirstOrDefault(e => e.EpisodeNumber == targetEpisode.Value);
            if (ep != null)
            {
                TvSelectedEpisode = ep;
                StatusText = $"Jumped to S{TvSelectedSeason:D2}E{targetEpisode.Value:D2} — {ep.Title}";
            }
            else
            {
                StatusText = $"Episode {targetEpisode.Value} not found in Season {TvSelectedSeason}. " +
                             $"Try selecting the season first then re-entering.";
            }
        }
    }

    public ICommand PickTvEpisodeCommand { get; private set; } = null!;
    public ICommand HideTvPickerCommand  { get; private set; } = null!;

    // ── Decrypted API key helpers — DPAPI transparent to all callers ──────────
    private string TmdbKey => Services.SecureKeyService.Decrypt(Settings.TmdbApiKey);
    private string OmdbKey => Services.SecureKeyService.Decrypt(Settings.OmdbApiKey);

    /// <summary>Call after any Settings property change to trigger auto-save.</summary>
    public void OnSettingChanged() => ScheduleSettingsSave();

    // Wrapper so toggling the checkbox wires up / tears down the watcher immediately
    /// <summary>
    /// When enabled, monitors the Library folder for new video files and adds them
    /// to the Library grid automatically without requiring a full re-scan.
    /// </summary>
    public bool LibraryWatchEnabled
    {
        get => Settings.LibraryWatchEnabled;
        set
        {
            Settings.LibraryWatchEnabled = value;
            RaiseProperty();
            ApplyLibraryWatchSetting();
            _ = App.ConfigService.SaveAsync();
        }
    }

    public int LibraryWatchPollMinutes
    {
        get => Settings.LibraryWatchPollMinutes;
        set
        {
            Settings.LibraryWatchPollMinutes = Math.Max(1, value);
            RaiseProperty();
            if (Settings.LibraryWatchEnabled) ApplyLibraryWatchSetting();
            _ = App.ConfigService.SaveAsync();
        }
    }

    /// <summary>
    /// When enabled, shows the Duplicates tab in the main tab bar.
    /// </summary>
    public bool ShowDuplicatesTab
    {
        get => Settings.ShowDuplicatesTab;
        set
        {
            Settings.ShowDuplicatesTab = value;
            RaiseProperty();
            RaiseProperty(nameof(DuplicatesTabVisible));
            _ = App.ConfigService.SaveAsync();
        }
    }

    public System.Windows.Visibility DuplicatesTabVisible =>
        Settings.ShowDuplicatesTab
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

    /// <summary>
    /// Policy for how orphan .vme_tmp_* / .vme_bak_* files are handled.
    /// "Ask" = RecoveryDialog  |  "AutoDelete" = silent delete  |  "Ignore" = skip scan
    /// </summary>
    public string OrphanTempFilePolicy
    {
        get => Settings.OrphanTempFilePolicy;
        set
        {
            Settings.OrphanTempFilePolicy = value;
            RaiseProperty();
            _ = App.ConfigService.SaveAsync();
        }
    }

    public bool WatchFolderEnabled
    {
        get => Settings.WatchFolderEnabled;
        set
        {
            Settings.WatchFolderEnabled = value;
            RaiseProperty();
            ApplyWatchFolderSetting();
            _ = App.ConfigService.SaveAsync();
        }
    }

    // ── Rename safety settings ───────────────────────────────────────────────────
    public bool AutoRenameEnabled
    {
        get => Settings.AutoRenameEnabled;
        set { Settings.AutoRenameEnabled = value; RaiseProperty(); _ = App.ConfigService.SaveAsync(); }
    }

    public bool ConfirmTypeMismatchRename
    {
        get => Settings.ConfirmTypeMismatchRename;
        set { Settings.ConfirmTypeMismatchRename = value; RaiseProperty(); _ = App.ConfigService.SaveAsync(); }
    }

    // ── MKV Artwork Engine status ────────────────────────────────────────────────
    public string MkvPropEditStatus
    {
        get
        {
            if (Services.MkvPropEditService.IsAvailable)
            {
                var ver = string.IsNullOrWhiteSpace(Services.MkvPropEditService.Version)
                    ? string.Empty
                    : System.Environment.NewLine + "    " + Services.MkvPropEditService.Version;
                return "✓  mkvpropedit found:" + System.Environment.NewLine
                     + "    " + Services.MkvPropEditService.ExePath
                     + ver + System.Environment.NewLine
                     + "MKV artwork uses mkvpropedit for proper Matroska cover.jpg embedding.";
            }

            var nativeDir = Services.NativeLibraryExtractor.NativeDir;
            return "✗  mkvpropedit not found." + System.Environment.NewLine
                 + "Searched: native\\ folder, %ProgramFiles%\\MKVToolNix\\, Scoop, PATH." + System.Environment.NewLine
                 + $"Quickest fix: drop mkvpropedit.exe into:{System.Environment.NewLine}    {nativeDir}" + System.Environment.NewLine
                 + "Or install MKVToolNix and click 🔄 Re-detect." + System.Environment.NewLine
                 + "Without it, TagLib# handles MKV artwork (less reliable with Plex/Jellyfin).";
        }
    }

    public ICommand RedetectMkvPropEditCommand => new RelayCommand(_ =>
    {
        Services.MkvPropEditService.Redetect();
        RaiseProperty(nameof(MkvPropEditStatus));
    });

    // ── Language settings ─────────────────────────────────────────────────────
    public System.Collections.ObjectModel.ObservableCollection<string> SupportedLanguageDisplayNames =>
        new(Services.LanguageService.SupportedLanguages
            .Select(l => Services.LanguageService.GetDisplayName(l.Code)));

    public int LanguageSelectedIndex
    {
        get
        {
            var code = Settings.LanguageCode ?? "System";
            var idx  = Services.LanguageService.SupportedLanguages
                .Select((l, i) => (l.Code, i))
                .FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)).i;
            return Math.Max(0, idx);
        }
        set
        {
            if (value < 0 || value >= Services.LanguageService.SupportedLanguages.Count) return;
            var newCode = Services.LanguageService.SupportedLanguages[value].Code;
            Settings.LanguageCode = newCode;
            _ = App.ConfigService.SaveAsync();

            // Apply live — DynamicResource bindings update immediately
            Services.LanguageService.Apply(newCode);
            RaiseProperty();
            StatusText = $"✓ Language set to {Services.LanguageService.GetDisplayName(newCode)}.";
        }
    }

    public bool AddFolderRecursive
    {
        get => Settings.AddFolderRecursive;
        set { Settings.AddFolderRecursive = value; RaiseProperty(); _ = App.ConfigService.SaveAsync(); }
    }

    public bool WatchFolderRecursive
    {
        get => Settings.WatchFolderRecursive;
        set
        {
            Settings.WatchFolderRecursive = value;
            RaiseProperty();
            // Restart watch with new recursive setting
            if (Settings.WatchFolderEnabled) ApplyWatchFolderSetting();
            _ = App.ConfigService.SaveAsync();
        }
    }

    // ── TV Tree View commands ─────────────────────────────────────────────────
    public ICommand TreeLoadShowCommand    { get; private set; } = null!;
    public ICommand TreeLoadSeasonCommand  { get; private set; } = null!;
    public ICommand TreeLoadEpisodeCommand { get; private set; } = null!;
    public ICommand TreeMarkWatchedCommand { get; private set; } = null!;
    public ICommand TreeMarkUnwatchedCommand { get; private set; } = null!;
    public ICommand TreeOpenLocationCommand  { get; private set; } = null!;
    public ICommand TreeCopyPathCommand      { get; private set; } = null!;

    /// <summary>Called from code-behind to refresh TvShowTree after watched state changes.</summary>
    public void NotifyTvTreeChanged() => RaiseProperty(nameof(TvShowTree));

    private void InitTreeCommands()
    {
        TreeLoadShowCommand = new RelayCommand(p =>
        {
            if (p is Models.TvShowNode show)
            {
                var entries = show.Seasons
                    .SelectMany(s => s.Episodes)
                    .Where(e => !e.IsMissingEpisode && System.IO.File.Exists(e.FilePath))
                    .ToList();
                foreach (var e in entries) TryAddFile(e.FilePath);
                StatusText = $"Loaded {entries.Count} episode(s) from {show.ShowTitle}";
            }
        });

        TreeLoadSeasonCommand = new RelayCommand(p =>
        {
            if (p is Models.TvSeasonNode season)
            {
                var entries = season.Episodes
                    .Where(e => !e.IsMissingEpisode && System.IO.File.Exists(e.FilePath))
                    .ToList();
                foreach (var e in entries) TryAddFile(e.FilePath);
                StatusText = $"Loaded {entries.Count} episode(s) from {season.SeasonLabel}";
            }
        });

        TreeLoadEpisodeCommand = new RelayCommand(p =>
        {
            if (p is Models.LibraryEntry entry && !entry.IsMissingEpisode)
                TryAddFile(entry.FilePath);
        });

        TreeMarkWatchedCommand = new RelayCommand(p =>
        {
            var entries = GetTreeEntries(p);
            foreach (var e in entries) e.IsWatched = true;
            RaiseProperty(nameof(TvShowTree));
        });

        TreeMarkUnwatchedCommand = new RelayCommand(p =>
        {
            var entries = GetTreeEntries(p);
            foreach (var e in entries) e.IsWatched = false;
            RaiseProperty(nameof(TvShowTree));
        });

        TreeOpenLocationCommand = new RelayCommand(p =>
        {
            var path = GetTreePath(p);
            if (!string.IsNullOrWhiteSpace(path) && System.IO.File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe",
                    string.Format("/select,\"{0}\"", path));
        });

        TreeCopyPathCommand = new RelayCommand(p =>
        {
            var path = GetTreePath(p);
            if (!string.IsNullOrWhiteSpace(path))
                System.Windows.Clipboard.SetText(path);
        });
    }

    private static List<Models.LibraryEntry> GetTreeEntries(object? p) => p switch
    {
        Models.TvShowNode show     => show.Seasons.SelectMany(s => s.Episodes).Where(e => !e.IsMissingEpisode).ToList(),
        Models.TvSeasonNode season => season.Episodes.Where(e => !e.IsMissingEpisode).ToList(),
        Models.LibraryEntry entry  => entry.IsMissingEpisode ? new() : new() { entry },
        _                          => new()
    };

    private static string GetTreePath(object? p) => p switch
    {
        Models.LibraryEntry entry => entry.FilePath,
        _ => string.Empty
    };

    // ── Auto-embed settings ───────────────────────────────────────────────────
    public bool AutoEmbedEnabled
    {
        get => Settings.AutoEmbedEnabled;
        set { Settings.AutoEmbedEnabled = value; RaiseProperty(); _ = App.ConfigService.SaveAsync(); }
    }

    /// <summary>0=Low(40%), 1=Medium(60%), 2=High(80%), 3=ExactOnly(100%)</summary>
    public int AutoEmbedConfidenceLevel
    {
        get => Settings.AutoEmbedConfidenceLevel;
        set { Settings.AutoEmbedConfidenceLevel = Math.Clamp(value, 0, 3); RaiseProperty(); _ = App.ConfigService.SaveAsync(); }
    }

    public string AutoEmbedConfidenceLabel => Settings.AutoEmbedConfidenceLevel switch
    {
        0 => "Low — 40% title match required",
        1 => "Medium — 60% title match required",
        2 => "High — 80% title match required",
        3 => "Exact — only embed on exact title match",
        _ => "High"
    };

    public string LastAutoEmbedTime => string.IsNullOrWhiteSpace(Settings.LastAutoEmbedTime)
        ? "Never" : Settings.LastAutoEmbedTime;

    public int WatchFolderPollMinutes
    {
        get => Settings.WatchFolderPollMinutes;
        set
        {
            Settings.WatchFolderPollMinutes = Math.Max(1, value);
            RaiseProperty();
            _watchFolderService.UpdatePollInterval(Math.Max(1, value));
            _ = App.ConfigService.SaveAsync();
        }
    }

    // ── API Key Lock State (locked after successful validation) ───────────────

    private bool _tmdbKeyLocked;
    public bool TmdbKeyLocked { get => _tmdbKeyLocked; set => Set(ref _tmdbKeyLocked, value); }

    private bool _omdbKeyLocked;
    public bool OmdbKeyLocked { get => _omdbKeyLocked; set => Set(ref _omdbKeyLocked, value); }

    // ── API Validation Flash State ────────────────────────────────────────────
    // null = idle, true = valid (green ✓), false = invalid (red ✗)

    private bool? _tmdbKeyValid;
    public bool? TmdbKeyValid { get => _tmdbKeyValid; set => Set(ref _tmdbKeyValid, value); }

    private bool? _omdbKeyValid;
    public bool? OmdbKeyValid { get => _omdbKeyValid; set => Set(ref _omdbKeyValid, value); }

    // ── Theme ─────────────────────────────────────────────────────────────────

    private bool _isDark = true;
    public bool IsDark { get => _isDark; set => Set(ref _isDark, value); }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand AddFilesCommand         { get; }
    public ICommand AddFolderCommand        { get; }
    public ICommand RemoveFileCommand       { get; }
    public ICommand ClearFilesCommand       { get; }
    public ICommand RefreshFilesCommand     { get; }
    public ICommand DeleteFileCommand       { get; }
    public ICommand SearchApiCommand        { get; }
    public ICommand LoadMoreResultsCommand  { get; }
    public ICommand ClearSearchCommand     { get; }
    public ICommand LookupByIdCommand       { get; }
    public ICommand ApplyRetrievedCommand   { get; }
    public ICommand ApplyMetadataCommand    { get; }
    public ICommand UndoLastEmbedCommand    { get; }
    public ICommand BatchProcessCommand     { get; }
    public ICommand BatchTvCommand          { get; private set; } = null!;
    public ICommand BatchRenameOnlyCommand  { get; }
    public ICommand RenameCurrentFileCommand { get; private set; } = null!;
    public ICommand CancelOperationCommand       { get; }
    public ICommand BrowseFallbackAppCommand     { get; }
    public ICommand ClearFallbackAppCommand      { get; }
    public ICommand SaveCopySettingsCommand      { get; }
    public ICommand ClearSavedCopyDestCommand    { get; }
    public ICommand JumpToNextFailureCommand { get; }
    public ICommand PickArtworkCommand           { get; }
    public ICommand CopyArtworkToClipboardCommand { get; private set; } = null!;
    public ICommand BulkArtworkCommand      { get; }
    public ICommand ToggleThemeCommand      { get; }
    public ICommand SaveSettingsCommand          { get; }
    public ICommand SaveMoveCopySettingsCommand { get; private set; } = null!;
    public ICommand ValidateKeysCommand     { get; }
    public ICommand SelectAllCommand        { get; }
    public ICommand DeselectAllCommand      { get; }
    public ICommand SelectWatchedCommand    { get; }
    public ICommand SelectUnwatchedCommand  { get; }
    public ICommand ToggleWatchedCommand    { get; }
    public ICommand UnlockTmdbCommand       { get; }
    public ICommand UnlockOmdbCommand       { get; }
    public ICommand CopyFilesCommand        { get; }
    public ICommand MoveFilesCommand        { get; }
    public ICommand BrowseCopyDestCommand   { get; }
    public ICommand AnalyzeSpaceCommand     { get; }
    public ICommand ExportCsvCommand         { get; }
    public ICommand ExportNfoCommand         { get; }
    public ICommand FindDuplicatesCommand    { get; }
    public ICommand ApplyRenamePresetCommand { get; }
    public ICommand ToggleLockCommand        { get; }
    public ICommand ToggleSelectedLockCommand { get; }

    // ── Library commands ──────────────────────────────────────────────────────
    public ICommand ScanLibraryCommand         { get; private set; } = null!;
    public ICommand BrowseLibraryFolderCommand { get; private set; } = null!;
    public ICommand ExportLibraryNfoCommand    { get; private set; } = null!;
    public ICommand ExportLibraryCsvCommand    { get; private set; } = null!;
    public ICommand ExportLibraryXlsxCommand   { get; private set; } = null!;
    public ICommand ClearLibraryCacheCommand   { get; private set; } = null!;
    public AsyncRelayCommand<Models.LibraryEntry> LibraryRowDoubleClickCommand { get; private set; } = null!;
    public ICommand LoadLibrarySelectionCommand { get; private set; } = null!;

    // ── Constructor ───────────────────────────────────────────────────────────

    public MainViewModel()
    {
        // ── Initialise services the library scan/cache paths depend on ────────
        // MUST happen before any code that touches the library, since the scan
        // service wraps the cache service and both are used throughout this VM.
        _libraryCacheService = new Services.LibraryCacheService();
        _libraryScanService  = new Services.LibraryScanService(_metadataService, _libraryCacheService);

        // ── Instantiate sub-ViewModels ─────────────────────────────────────────
        Transfer = new CopyMoveViewModel(Settings, () => Files);
        Search   = new SearchViewModel(_apiService, _aniListService, () => TmdbKey, () => OmdbKey, m => RetrievedMetadata = m, WpfUiDispatcher.FromCurrent());
        Metadata = new MetadataViewModel(Settings, _metadataService, _renameService, WpfUiDispatcher.FromCurrent());

        // ── Wire sub-VM delegates → MainViewModel implementations ──────────────
        Transfer.ExecuteTransferAsync  = ExecuteCopyMoveAsync;
        Transfer.BrowseDestination     = () => { var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select copy/move destination" }; if (dlg.ShowDialog() == true) { CopyDestination = dlg.FolderName; Transfer.Destination = dlg.FolderName; } };

        Search.OnApplyRetrieved        = meta => { RetrievedMetadata = meta; ApplyRetrievedToEditing(); };
        Search.EpisodeApplied         += m => { RetrievedMetadata = m; };

        Metadata.ApplyToFileAsync         = ApplyToFileAsync;
        Metadata.UndoLastEmbedAsync       = UndoLastEmbedAsync;
        Metadata.RenameCurrentFileAsync   = RenameCurrentFileAsync;
        Metadata.BatchRenameOnlyAsync     = BatchRenameOnlyAsync;
        Metadata.ExportNfoAsync           = ExportNfoAsync;
        Metadata.BulkDownloadArtworkAsync = BulkDownloadArtworkAsync;
        Metadata.CopyArtworkToClipboard   = CopyArtworkToClipboard;
        Metadata.PickArtwork              = PickArtwork;
        Metadata.JumpToNextFailure        = JumpToNextFailure;
        Metadata.ToggleLockAsync          = ToggleLockCurrentFileAsync;

        // ── Wire sub-VM events → MainViewModel events (for banner notifications) ─
        Metadata.RenameSucceeded += (s, e) => RenameSucceeded?.Invoke(s, e);
        Metadata.RenameFailed    += (s, e) => RenameFailed?.Invoke(s, e);
        Metadata.EmbedSucceeded  += (s, e) => EmbedSucceeded?.Invoke(s, e);
        Transfer.CopyCompleted   += (s, e) => CopyCompleted?.Invoke(s, e);
        Transfer.MoveCompleted   += (s, e) => MoveCompleted?.Invoke(s, e);

        HookFilesCollection();
        IsDark = App.IsDarkTheme;

        // Restore API key lock state from previous session
        if (Settings.TmdbKeyValidated && !string.IsNullOrWhiteSpace(Settings.TmdbApiKey))
        {
            TmdbKeyLocked = true;
            TmdbKeyValid  = true;
        }
        if (Settings.TraktConnected && !string.IsNullOrWhiteSpace(Settings.TraktClientId))
        {
            TraktClientIdLocked     = true;
            TraktClientSecretLocked = true;
        }
        if (Settings.OpenSubsKeyValidated && !string.IsNullOrWhiteSpace(Settings.OpenSubtitlesApiKey))
        {
            OpenSubsKeyLocked = true;
            OpenSubsKeyValid  = true;
        }
        _subtitleSearchLanguages = Settings.OpenSubsLastLanguages;
        if (Settings.OmdbKeyValidated && !string.IsNullOrWhiteSpace(OmdbKey))
        {
            OmdbKeyLocked = true;
            OmdbKeyValid  = true;
        }

        AddFilesCommand          = new RelayCommand(_ => AddFiles());
        AddFolderCommand         = new RelayCommand(_ => AddFolder());
        RemoveFileCommand        = new RelayCommand(_ => RemoveSelected(), _ => SelectedFile != null);
        ClearFilesCommand        = new RelayCommand(_ => ClearAllFiles(), _ => Files.Any());
        RefreshFilesCommand      = new AsyncRelayCommand(RefreshFilesAsync, _ => Files.Any() && !IsBusy);
        DeleteFileCommand        = new RelayCommand(_ => RequestDeleteFile(), _ => SelectedFile != null);
        SearchApiCommand         = new AsyncRelayCommand(SearchApiAsync, _ => !IsBusy);
        LoadMoreResultsCommand   = new AsyncRelayCommand(LoadMoreResultsAsync, _ => !IsBusy && HasMoreResults);
        ClearSearchCommand       = new RelayCommand(_ =>
        {
            SearchResults.Clear();
            _allSearchResults.Clear();
            _searchPage = 0;
            HasMoreResults = false;
            RetrievedMetadata = null;
            StatusText = "Search results cleared.";
        }, _ => SearchResults.Any() || RetrievedMetadata != null);
        LookupByIdCommand        = new AsyncRelayCommand(LookupByIdAsync, _ => !IsBusy);
        ApplyRetrievedCommand    = new RelayCommand(_ => ApplyRetrievedToEditing(), _ => RetrievedMetadata != null);
        ApplyMetadataCommand     = new AsyncRelayCommand(ApplyToFileAsync, _ => SelectedFile != null && !IsBusy);
        UndoLastEmbedCommand     = new AsyncRelayCommand(UndoLastEmbedAsync, _ => SelectedFile?.CanUndo == true && !IsBusy);
        UndoBatchCommand         = new AsyncRelayCommand(UndoBatchAsync, _ => CanUndoBatch);
        InitTreeCommands();
        InitOpenSubsCommands();
        InitTraktCommands();
        BatchTvCommand           = new AsyncRelayCommand(BatchTvAsync,
            _ => Files.Any(f => f.IsSelected && !f.IsSeparator) && !IsBusy);
        BatchProcessCommand      = new AsyncRelayCommand(BatchProcessAsync, _ => Files.Any(f => f.IsSelected && !f.IsSeparator) && !IsBusy);
        BatchRenameOnlyCommand   = new AsyncRelayCommand(BatchRenameOnlyAsync,
            _ => Files.Any(f => f.IsSelected && !f.IsSeparator) && !IsBusy && !string.IsNullOrWhiteSpace(Settings.RenamePattern));
        CancelOperationCommand       = new RelayCommand(_ => RequestCancel(), _ => IsBusy && !IsCancelling);
        BrowseFallbackAppCommand     = new RelayCommand(_ => BrowseFallbackApp());
        ClearFallbackAppCommand      = new RelayCommand(_ => ClearFallbackApp(),
            _ => !string.IsNullOrWhiteSpace(Settings.FallbackAppPath));
        SaveCopySettingsCommand      = new RelayCommand(_ => SaveCopySettings());
        ClearSavedCopyDestCommand    = new RelayCommand(_ => ClearSavedCopyDest(),
            _ => !string.IsNullOrWhiteSpace(Settings.LastCopyDestination));
        JumpToNextFailureCommand = new RelayCommand(_ => JumpToNextFailure(),
            _ => Files.Any(f => !f.IsSeparator && f.HasError));
        PickArtworkCommand            = new RelayCommand(_ => PickArtwork());
        CopyArtworkToClipboardCommand = new RelayCommand(_ => CopyArtworkToClipboard(),
            _ => EditingMetadata?.ArtworkBytes is { Length: > 0 });
        BulkArtworkCommand       = new AsyncRelayCommand(BulkDownloadArtworkAsync, _ => Files.Any(f => f.IsSelected && !f.IsSeparator) && !IsBusy);
        ToggleThemeCommand       = new RelayCommand(_ => ToggleTheme());
        SaveMoveCopySettingsCommand = new RelayCommand(_ =>
        {
            App.ConfigService.Save();
            StatusText = $"✓ Move/Copy settings saved — Destination: {Settings.LastCopyDestination}  ·  Conflict: {Settings.LastConflictMode}";
            Log( $"[{DateTime.Now:HH:mm:ss}] 💾 Move/Copy settings saved.");
        });
        SaveSettingsCommand      = new RelayCommand(_ =>
        {
            App.ConfigService.Save();
            StatusText = "✓ Settings saved — including Move/Copy destination and conflict mode.";
            Log( $"[{DateTime.Now:HH:mm:ss}] 💾 Settings saved (Move/Copy: dest={Settings.LastCopyDestination}, conflict={Settings.LastConflictMode}).");
        });
        ValidateKeysCommand      = new AsyncRelayCommand(ValidateKeysAsync, _ => !IsBusy);
        SelectAllCommand         = new RelayCommand(_ => { foreach (var f in Files) if (!f.IsSeparator) f.IsSelected = true; });
        DeselectAllCommand       = new RelayCommand(_ => { foreach (var f in Files) if (!f.IsSeparator) f.IsSelected = false; });
        SelectWatchedCommand     = new RelayCommand(_ => { foreach (var f in Files) if (!f.IsSeparator) f.IsSelected = f.IsWatched; });
        SelectUnwatchedCommand   = new RelayCommand(_ => { foreach (var f in Files) if (!f.IsSeparator) f.IsSelected = !f.IsWatched; });
        ToggleWatchedCommand     = new RelayCommand(_ =>
        {
            if (SelectedFile == null) return;
            SelectedFile.IsWatched = !SelectedFile.IsWatched;
            // Keep EmbeddedMetadata in sync so the flag is written on next embed
            SelectedFile.EmbeddedMetadata.IsWatched = SelectedFile.IsWatched;
            EditingMetadata.IsWatched                = SelectedFile.IsWatched;
        });
        UnlockTmdbCommand = new RelayCommand(_ =>
        {
            TmdbKeyLocked = false; TmdbKeyValid = null;
            Settings.TmdbKeyValidated = false;
            _ = App.ConfigService.SaveAsync();
        });
        UnlockOmdbCommand = new RelayCommand(_ =>
        {
            OmdbKeyLocked = false; OmdbKeyValid = null;
            Settings.OmdbKeyValidated = false;
            _ = App.ConfigService.SaveAsync();
        });
        CopyFilesCommand         = new AsyncRelayCommand(() => ExecuteCopyMoveAsync(CopyMode.Copy), _ => CanCopyMove);
        MoveFilesCommand         = new AsyncRelayCommand(() => ExecuteCopyMoveAsync(CopyMode.Move), _ => CanCopyMove);
        BrowseCopyDestCommand    = new RelayCommand(_ => BrowseCopyDestination());
        AnalyzeSpaceCommand      = new AsyncRelayCommand(AnalyzeSpaceAsync,
            _ => Files.Any(f => f.IsSelected && !f.IsSeparator) && !string.IsNullOrWhiteSpace(CopyDestination) && !IsBusy);
        ExportCsvCommand         = new AsyncRelayCommand(ExportCsvAsync, _ => Files.Any() && !IsBusy);
        ExportNfoCommand         = new AsyncRelayCommand(ExportNfoAsync, _ => Files.Any(f => f.IsSelected && !f.IsSeparator) && !IsBusy);
        FindDuplicatesCommand    = new RelayCommand(_ => FindDuplicates(), _ => Files.Count > 1 && !IsBusy);
        ApplyRenamePresetCommand = new RelayCommand(p => { if (p is string preset) Settings.RenamePattern = preset; });
        ToggleLockCommand        = new RelayCommand(_ => ToggleLockCurrentFile(), _ => SelectedFile != null && !IsBusy);
        ToggleSelectedLockCommand = new RelayCommand(p =>
        {
            // WPF CommandParameter always arrives as a string from XAML
            bool lockIt = p switch
            {
                bool b     => b,
                "True"     => true,
                "False"    => false,
                "Lock"     => true,
                "Unlock"   => false,
                _          => true
            };
            int count = 0;
            foreach (var vf in Files.Where(f => f.IsSelected && !f.IsSeparator))
            {
                vf.TrySetReadOnly(lockIt);
                count++;
            }
            StatusText = lockIt
                ? $"🔒 Locked {count} file(s)."
                : $"🔓 Unlocked {count} file(s).";
            Log( $"[{DateTime.Now:HH:mm:ss}] {StatusText}");
        }, _ => Files.Any(f => f.IsSelected && !f.IsSeparator));

        // Detect copy engines at startup
        Services.FileCopyService.DetectEngines();

        // Clean up any orphaned temp/backup files left by previous crashes
        // Startup recovery is triggered via TriggerStartupRecovery() after window loads

        // Initialise LibraryColumns (services already initialized above)
        InitLibraryColumns();

        // Duplicates tab ViewModel
        DuplicatesVM = new DuplicatesViewModel(_metadataService, new Services.WpfDialogService(), WpfUiDispatcher.FromCurrent());
        DuplicatesVM.FileDeleted  += (_, msg) =>
            Log( $"[{DateTime.Now:HH:mm:ss}] 🗑 {msg}");
        DuplicatesVM.ErrorOccurred += (_, err) =>
        {
            StatusText = $"Duplicates: {err}";
            Log( $"[{DateTime.Now:HH:mm:ss}] ✕ Duplicates error: {err}");
        };

        // Wire WatchFolderService events
        _watchFolderService.FileDetected += path =>
        {
            TryAddWatchedFile(path);
            var added = Files.FirstOrDefault(f => !f.IsSeparator &&
                f.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (added != null)
                Log( $"[{DateTime.Now:HH:mm:ss}] 📁 Watch folder: {added.FileName}");
        };
        _watchFolderService.WatcherError += msg =>
            Log( $"[{DateTime.Now:HH:mm:ss}] ⚠ Watch folder: {msg}");

        // Library commands
        // Auto-scan on startup if a library folder is configured.
        // Uses a short dispatcher delay so the window is fully rendered first.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(async () =>
            {
                if (!string.IsNullOrWhiteSpace(Settings.LibraryFolderPath) &&
                    Directory.Exists(Settings.LibraryFolderPath) &&
                    !IsBusy)
                {
                    await ScanLibraryAsync();
                }
                else if (LibraryEntries.Any())
                {
                    // Entries exist from a previous session cache — just build tabs
                    RebuildLibraryTabs();
                }
                // Fire update check after startup work completes — low priority, non-blocking
                _ = CheckForUpdateAsync();
            }));

        ScanLibraryCommand         = new AsyncRelayCommand(ScanLibraryAsync,
            _ => CanScanLibrary);
        BrowseLibraryFolderCommand = new RelayCommand(_ => BrowseLibraryFolder());
        ExportLibraryNfoCommand    = new AsyncRelayCommand(ExportLibraryNfoAsync,
            _ => LibraryEntries.Count > 0 && !IsBusy);
        ExportLibraryCsvCommand    = new AsyncRelayCommand(ExportLibraryCsvAsync,
            _ => LibraryEntries.Any());
        ExportLibraryXlsxCommand   = new AsyncRelayCommand(ExportLibraryXlsxAsync,
            _ => LibraryEntries.Any());
        ClearLibraryCacheCommand          = new RelayCommand(_ => ClearLibraryCache());
        SaveLibraryColumnLayoutCommand    = new RelayCommand(_ =>
        {
            // Sync live DataGrid widths/order into LibraryColumns before saving
            // so a column resize without a reorder is also captured correctly.
            SyncLibraryGridBeforeSave?.Invoke();
            SaveLibraryColumnLayoutWithConfirmation();
        });
        PickTvEpisodeCommand              = new AsyncRelayCommand(PickTvEpisodeAsync);
        RenameCurrentFileCommand          = new AsyncRelayCommand(RenameCurrentFileAsync,
            _ => SelectedFile != null && !IsBusy);
        HideTvPickerCommand               = new RelayCommand(_ =>
        {
            IsTvPickerVisible = false;
            TvSeasonNumbers.Clear();
            TvEpisodeItems.Clear();
            TvSelectedEpisode = null;
        });
        AddExtraLibraryFolderCommand      = new RelayCommand(_ => AddExtraLibraryFolder());

        CloseLibraryTabCommand = new RelayCommand(p =>
        {
            if (p is not Models.LibraryTab tab) return;

            // Remove entries from master list that belong to this tab
            var toRemove = LibraryEntries
                .Where(e => e.FilePath.StartsWith(tab.FolderPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var e in toRemove) LibraryEntries.Remove(e);

            // Remove from tabs list
            LibraryTabs.Remove(tab);

            // If this was a root extra folder, remove it from ExtraLibraryFolders too
            var matchedRoot = ExtraLibraryFolders.FirstOrDefault(f =>
                string.Equals(f, tab.FolderPath, StringComparison.OrdinalIgnoreCase) ||
                tab.FolderPath.StartsWith(f.TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
            if (matchedRoot != null && string.Equals(matchedRoot, tab.FolderPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                ExtraLibraryFolders.Remove(matchedRoot);
                Settings.ExtraLibraryFolders.Remove(matchedRoot);
                StatusText = $"Removed folder '{Path.GetFileName(tab.FolderPath)}' from library.";
            }
            else
            {
                // It was a subfolder tab — if primary folder, clear it too
                if (string.Equals(tab.FolderPath, Settings.LibraryFolderPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    Settings.LibraryFolderPath = string.Empty;
                    RaiseProperty(nameof(LibraryFolderDisplay));
                    StatusText = "Primary library folder removed.";
                }
                else
                    StatusText = $"Tab '{tab.Header}' closed.";
            }

            // Select next available tab
            if (SelectedLibraryTab == tab)
                SelectedLibraryTab = LibraryTabs.FirstOrDefault();

            RaiseProperty(nameof(HasMultipleTabs));
            LibraryView?.Refresh();
            _ = App.ConfigService.SaveAsync();
        }, p => p is Models.LibraryTab);
        SetTabAsMoviesCommand = new RelayCommand(p =>
        {
            if (p is Models.LibraryTab t)
            {
                t.IsTvOverride = false;
                if (!string.IsNullOrWhiteSpace(t.FolderPath))
                    Settings.TabTypeOverrides[t.FolderPath] = false;
                _ = App.ConfigService.SaveAsync();
                if (SelectedLibraryTab == t) { RaiseProperty(nameof(ActiveTabIsTv)); ApplyTabColumns(); }
            }
        });

        SetTabAsTvCommand = new RelayCommand(p =>
        {
            if (p is Models.LibraryTab t)
            {
                t.IsTvOverride = true;
                if (!string.IsNullOrWhiteSpace(t.FolderPath))
                    Settings.TabTypeOverrides[t.FolderPath] = true;
                _ = App.ConfigService.SaveAsync();
                if (SelectedLibraryTab == t) { RaiseProperty(nameof(ActiveTabIsTv)); ApplyTabColumns(); }
            }
        });

        ResetTabTypeCommand = new RelayCommand(p =>
        {
            if (p is Models.LibraryTab t)
            {
                t.IsTvOverride = null;
                if (!string.IsNullOrWhiteSpace(t.FolderPath))
                    Settings.TabTypeOverrides.Remove(t.FolderPath);
                _ = App.ConfigService.SaveAsync();
                if (SelectedLibraryTab == t) { RaiseProperty(nameof(ActiveTabIsTv)); ApplyTabColumns(); }
            }
        });

        RemoveExtraLibraryFolderCommand   = new RelayCommand(p =>
        {
            if (p is string folder)
            {
                ExtraLibraryFolders.Remove(folder);
                Settings.ExtraLibraryFolders.Remove(folder);
                _ = App.ConfigService.SaveAsync();
            }
        }, p => p is string);

        // Restore extra folders from settings
        foreach (var f in Settings.ExtraLibraryFolders
            .Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f)))
            ExtraLibraryFolders.Add(f);
        LibraryRowDoubleClickCommand = new AsyncRelayCommand<Models.LibraryEntry>(
            LibraryRowDoubleClickAsync);
        LoadLibrarySelectionCommand = new RelayCommand(
            p => LoadLibrarySelection(p as System.Collections.IList),
            p => (p as System.Collections.IList)?.Count > 0);

        // NOTE: Watch folder is NOT started here. The constructor runs before any files
        // are loaded into the FILES panel, so starting the watcher here would seed an
        // empty existingPaths and flood the user with every file in LastFolderPath on
        // the first poll tick. The watcher is started by AddFolderAsync (when a folder
        // is loaded) and by the WatchFolderEnabled property setter (when toggled in
        // Settings). Both call ApplyWatchFolderSetting() after Files is populated.

        // Restore Move/Copy tab settings from last session
        if (!string.IsNullOrWhiteSpace(Settings.LastCopyDestination)
            && Directory.Exists(Settings.LastCopyDestination))
        {
            _copyDestination = Settings.LastCopyDestination;
            RaiseProperty(nameof(CopyDestination));
            RaiseProperty(nameof(CanCopyMove));
            // SavedCopyDestination reads Settings.LastCopyDestination directly
            // so no extra assignment needed — just notify binding to re-read
            RaiseProperty(nameof(SavedCopyDestination));
        }
        if (!string.IsNullOrWhiteSpace(Settings.LastConflictMode))
        {
            _conflictMode = Settings.LastConflictMode;
            RaiseProperty(nameof(ConflictMode));
            RaiseProperty(nameof(ConflictModeDescription));
            RaiseProperty(nameof(SavedConflictMode));
        }

        // NOTE: startup orphan recovery is deferred to OnWindowLoaded() which is called
        // by MainWindow.OnLoaded — this guarantees RecoveryCandidatesFound is subscribed
        // before any orphan dialog fires. See TriggerStartupRecovery().

        // Wire library watch events
        _libraryWatchService.FileDetected += path =>
        {
            if (LibraryEntries.Any(e => e.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                return; // already in library
            _ = Task.Run(async () =>
            {
                try
                {
                    var entry = await _libraryScanService.BuildSingleEntryAsync(path);
                    if (entry == null) return;
                    System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                    {
                        if (!LibraryEntries.Any(e => e.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase)))
                        {
                            LibraryEntries.Add(entry);
                            Log( $"[{DateTime.Now:HH:mm:ss}] 📚 Library watch: {entry.FileName} added.");
                            StatusText = $"Library: {entry.FileName} detected.";
                        }
                    }, System.Windows.Threading.DispatcherPriority.Background);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LibraryWatch] {ex.GetType().Name}: {ex.Message}");
                }
            });
        };
        _libraryWatchService.WatcherError += msg =>
            Log( $"[{DateTime.Now:HH:mm:ss}] ⚠ Library watch: {msg}");

        // Apply library watch if enabled in previous session
        ApplyLibraryWatchSetting();

        EditingMetadata.PropertyChanged += (_, e) =>
        {
            UpdateRenamePreview();
            if (e.PropertyName is nameof(MovieMetadata.ImdbId) or nameof(MovieMetadata.TmdbId))
            {
                RaiseProperty(nameof(HasIdRefinement));
                RaiseProperty(nameof(IdRefinementHint));
            }
        };
    }

    // ── File Management ───────────────────────────────────────────────────────

    /// <summary>
    /// Loads a specific set of files into the FILES panel (used by the Health Check
    /// "Re-embed flagged files" action). Adds each path, reports a status line, and
    /// leaves them ready for the user to re-fetch metadata and Embed on the current build.
    /// </summary>
    public void LoadFilesForReembed(IReadOnlyList<string> paths)
    {
        if (paths == null || paths.Count == 0) return;
        int added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            TryAddFile(path);
            added++;
        }
        StatusText = added > 0
            ? $"Loaded {added} file(s) for re-embedding — search/apply metadata, then Embed."
            : "No valid files to load for re-embedding.";
        Log($"[{DateTime.Now:HH:mm:ss}] Health Check → loaded {added} semicolon-flagged file(s) for re-embed.");
    }

    public void AddFiles()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "Video Files|*.mp4;*.mkv;*.mov;*.wmv;*.m4v;*.webm|All Files|*.*"
        };

        // Restore last used directory
        if (!string.IsNullOrWhiteSpace(Settings.LastFilePath) &&
            Directory.Exists(Path.GetDirectoryName(Settings.LastFilePath)))
            dlg.InitialDirectory = Path.GetDirectoryName(Settings.LastFilePath);
        else if (!string.IsNullOrWhiteSpace(Settings.LastFolderPath) &&
            Directory.Exists(Settings.LastFolderPath))
            dlg.InitialDirectory = Settings.LastFolderPath;

        if (dlg.ShowDialog() != true) return;

        // Remember this location
        Settings.LastFilePath = dlg.FileNames.FirstOrDefault() ?? string.Empty;
        _ = App.ConfigService.SaveAsync();

        foreach (var path in dlg.FileNames)
            TryAddFile(path);

        // Restart watch folder on the folder the files came from
        if (Settings.WatchFolderEnabled && !string.IsNullOrWhiteSpace(Settings.LastFilePath))
        {
            var dir = Path.GetDirectoryName(Settings.LastFilePath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Settings.LastFolderPath = dir;
                ApplyWatchFolderSetting();
            }
        }
    }

    public async void AddFolder()
    {
        try { await AddFolderAsync(); }
        catch (Exception ex)
        {
            StatusText = $"Add Folder error: {ex.Message}";
            Log( $"[{DateTime.Now:HH:mm:ss}] ✕ AddFolder exception: {ex.Message}");
            EndOperation();
        }
    }

    private async Task AddFolderAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select folder containing video files",
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(Settings.LastFolderPath) &&
            Directory.Exists(Settings.LastFolderPath))
            dlg.InitialDirectory = Settings.LastFolderPath;

        if (dlg.ShowDialog() != true) return;
        await LoadFolderDirectAsync(dlg.FolderName);
    }

    /// <summary>
    /// Loads all video files from <paramref name="folderPath"/> into the FILES panel
    /// without showing a folder picker dialog. Used for both user-triggered loads
    /// (via AddFolderAsync) and automatic startup restore when Watch Folder is enabled.
    /// </summary>
    public async Task LoadFolderDirectAsync(string folderPath)
    {

        var ct = BeginOperation();
        IsBusy = true;
        ProgressValue = 0;
        StatusText = "Scanning folder…";

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp4", ".mkv", ".mov", ".wmv", ".m4v", ".webm" };

            Settings.LastFolderPath = folderPath;
        _ = App.ConfigService.SaveAsync();

        // Restart watch folder monitor on the new path
        ApplyWatchFolderSetting();

        // ── Phase 1: scan directory tree on background threads ─────────────────
        List<string> allFiles;
        try
        {
            allFiles = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var subDirs = new List<string> { folderPath };
                if (Settings.AddFolderRecursive)
                {
                    try
                    {
                        subDirs.AddRange(Directory.EnumerateDirectories(
                            folderPath, "*", SearchOption.AllDirectories));
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[FileLoad] {ex.GetType().Name}: {ex.Message}"); }
                }

                var found = new System.Collections.Concurrent.ConcurrentBag<string>();
                subDirs.AsParallel()
                       .WithDegreeOfParallelism(Math.Max(2, Environment.ProcessorCount))
                       .WithCancellation(ct)
                       .ForAll(dir =>
                       {
                           try
                           {
                               foreach (var f in Directory.EnumerateFiles(
                                   dir, "*.*", SearchOption.TopDirectoryOnly))
                               {
                                   var fn = Path.GetFileName(f);
                                   if (extensions.Contains(Path.GetExtension(f))
                                       && !fn.StartsWith(".vme_tmp_", StringComparison.OrdinalIgnoreCase)
                                       && !fn.StartsWith(".vme_bak_", StringComparison.OrdinalIgnoreCase))
                                       found.Add(f);
                               }
                           }
                           catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[FileLoad] {ex.GetType().Name}: {ex.Message}"); }
                       });

                return found.Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                            .ToList();
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Scan error: {ex.Message}";
            EndOperation();
            return;
        }

        if (allFiles.Count == 0)
        {
            StatusText = "No supported video files found in that folder.";
            EndOperation();
            return;
        }
        if (ct.IsCancellationRequested)
        {
            StatusText = "Folder scan cancelled.";
            EndOperation();
            return;
        }

        // Pre-filter already-loaded files so we don't re-read them
        var existingPaths = new HashSet<string>(
            Files.Select(f => f.FilePath), StringComparer.OrdinalIgnoreCase);
        var toLoad = allFiles.Where(f => !existingPaths.Contains(f)).ToList();

        StatusText = $"Found {allFiles.Count} file(s) — reading metadata…";
        int countBefore = Files.Count;

        // ── Phase 2: parallel metadata reading tuned to drive capability ──────
        //
        // Profile the drive so we use the right number of concurrent readers:
        //   NVMe  → up to 16 concurrent TagLib# reads (queue depth is huge)
        //   SSD   → up to 12 concurrent reads
        //   HDD   → 2–4  concurrent reads (seek penalty makes more threads slower)
        //   Net   → 2    concurrent reads
        //
        // UI chunk size also scales: larger = fewer Task.Yield() context switches,
        // smaller = more frequent progress bar updates. Tuned per drive type.
        var driveProfile = Services.DriveCapabilityService.GetProfile(folderPath);
        int metaWorkers  = driveProfile.RecommendedMetadataWorkers;

        // Chunk = how many VideoFile objects we buffer before pushing to UI list.
        // Larger chunks mean fewer dispatcher round-trips; capped so the first
        // files appear quickly even on huge folders.
        int uiChunkSize = driveProfile.IsSsd ? 200 : 100;

        Log( $"[{DateTime.Now:HH:mm:ss}] {driveProfile.Description}");
        StatusText = $"Loading {toLoad.Count} file(s) — {metaWorkers} parallel readers ({driveProfile.Description.Split('—')[0].Trim()})…";

        int processed = 0;
        int total     = toLoad.Count;

        // ── Each completed read dispatches itself immediately to the UI thread ──
        //
        // Previous approach: batch-and-poll on a thread-pool thread.
        // Problems: (1) batchFlushAt=200 meant small folders showed NOTHING until
        //               all reads finished; (2) ConfigureAwait(false) moved execution
        //               off the UI thread, so Files.Add() was called from a thread-pool
        //               thread — violating WPF ObservableCollection thread-affinity.
        //
        // New approach: each Task.Run() calls back to the UI dispatcher as soon as
        // its file is ready. Files appear one-by-one, immediately. For large folders
        // the dispatcher is called rapidly but WPF coalesces layout passes, so the
        // overhead is negligible vs. the perceived responsiveness gain.

        var dispatcher = System.Windows.Application.Current.Dispatcher;
        using var sem  = new SemaphoreSlim(metaWorkers);

        var readTasks = toLoad.Select((path, idx) => Task.Run(async () =>
        {
            await sem.WaitAsync(ct);
            if (ct.IsCancellationRequested) { sem.Release(); return; }
            try
            {
                var format = MetadataService.DetectFormat(path);
                var (supportsArt, supportsFullTags, warning) =
                    MetadataService.GetFormatCapabilities(format);
                var info     = new FileInfo(path);
                var embedded = _metadataService.ReadMetadataFast(path); // artwork loaded lazily
                var (parsedTitle, parsedYear) =
                    FilenameParser.Parse(Path.GetFileNameWithoutExtension(path));

                if (!string.IsNullOrWhiteSpace(parsedTitle))
                    embedded.Title = parsedTitle;
                else if (string.IsNullOrWhiteSpace(embedded.Title))
                    embedded.Title = Path.GetFileNameWithoutExtension(path);

                if (!string.IsNullOrWhiteSpace(parsedYear))
                    embedded.Year = parsedYear;

                var vf = new VideoFile
                {
                    FilePath         = path,
                    Format           = format,
                    FileSizeBytes    = info.Length,
                    SupportsArtwork  = supportsArt,
                    SupportsFullTags = supportsFullTags,
                    FormatWarning    = warning,
                    EmbeddedMetadata = embedded,
                    ParsedTitle      = parsedTitle,
                    ParsedYear       = parsedYear
                };
                vf.PendingMetadata = vf.EmbeddedMetadata.Clone();

                // Dispatch immediately to UI thread — file appears the moment it's read
                await dispatcher.InvokeAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    Files.Add(vf);
                    processed++;
                    ProgressValue = (int)((double)processed / total * 100);
                    StatusText    = $"Loading {processed} / {total} files…";
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch { /* skip unreadable files */ }
            finally { sem.Release(); }
        })).ToList();

        await Task.WhenAll(readTasks);

        int added = Files.Count(f => !f.IsSeparator) - Files.Take(countBefore).Count(f => !f.IsSeparator);
        EndOperation();
        int realCount = Files.Count(f => !f.IsSeparator);
        if (ct.IsCancellationRequested)
            StatusText = $"Scan cancelled — {realCount} file(s) loaded so far.";
        else
            StatusText = $"{realCount} file(s) loaded — {added} new.";

        if (added > 0)
            FilesLoaded?.Invoke(this,
                new FilesLoadedEventArgs(added, allFiles.Count, folderPath));
            RaiseProperty(nameof(WriteWorkerRecommendation));

        // Register all loaded files with the watch service so the poll timer
        // doesn't re-fire them as "new" on the next tick
        if (_watchFolderService.IsActive)
        {
            foreach (var f in Files.Where(x => !x.IsSeparator))
                _watchFolderService.AddKnownPath(f.FilePath);
        }

        // Check for incomplete operations in the loaded folder
        CheckForRecovery(new[] { folderPath });
    }


    // ══════════════════════════════════════════════════════════════════════════
    // OPENSUBTITLES
    // ══════════════════════════════════════════════════════════════════════════

    private readonly Services.OpenSubtitlesService _openSubsService = new();
    private string OpenSubsKey =>
        Services.SecureKeyService.Decrypt(Settings.OpenSubtitlesApiKey);

    private bool _openSubsKeyLocked;
    public bool OpenSubsKeyLocked
    {
        get => _openSubsKeyLocked;
        set => Set(ref _openSubsKeyLocked, value);
    }

    private bool? _openSubsKeyValid;
    public bool? OpenSubsKeyValid
    {
        get => _openSubsKeyValid;
        set => Set(ref _openSubsKeyValid, value);
    }

    public string OpenSubsKeyDisplay
    {
        get => _openSubsKeyLocked ? "●●●●●●●●●●●●●●●●●●●●  (saved)" : Settings.OpenSubtitlesApiKey;
        set { if (!_openSubsKeyLocked) Settings.OpenSubtitlesApiKey = value; }
    }

    // Subtitle search UI state
    private bool _isSearchingSubtitles;
    public bool IsSearchingSubtitles
    {
        get => _isSearchingSubtitles;
        set => Set(ref _isSearchingSubtitles, value);
    }

    private string _subtitleSearchLanguages = "en";
    private string _lastAutoQueryFile = string.Empty; // tracks which file populated SearchQuery
    public string SubtitleSearchLanguages
    {
        get => _subtitleSearchLanguages;
        set
        {
            Set(ref _subtitleSearchLanguages, value);
            Settings.OpenSubsLastLanguages = value;
            _ = App.ConfigService.SaveAsync();
        }
    }

    public System.Collections.ObjectModel.ObservableCollection<Services.OpenSubtitlesService.SubtitleSearchResult>
        SubtitleSearchResults { get; } = new();

    private Services.OpenSubtitlesService.SubtitleSearchResult? _selectedSubtitleResult;
    public Services.OpenSubtitlesService.SubtitleSearchResult? SelectedSubtitleResult
    {
        get => _selectedSubtitleResult;
        set => Set(ref _selectedSubtitleResult, value);
    }

    private string _subtitleDownloadStatus = string.Empty;
    public string SubtitleDownloadStatus
    {
        get => _subtitleDownloadStatus;
        set => Set(ref _subtitleDownloadStatus, value);
    }

    // Commands
    public ICommand ValidateOpenSubsKeyCommand  { get; private set; } = null!;
    public ICommand UnlockOpenSubsKeyCommand    { get; private set; } = null!;
    public ICommand SearchSubtitlesCommand      { get; private set; } = null!;
    public ICommand DownloadSubtitleCommand     { get; private set; } = null!;
    public ICommand ClearSubtitleResultsCommand { get; private set; } = null!;

    private void InitOpenSubsCommands()
    {
        ValidateOpenSubsKeyCommand = new AsyncRelayCommand(ValidateOpenSubsKeyAsync,
            _ => !string.IsNullOrWhiteSpace(Settings.OpenSubtitlesApiKey));

        UnlockOpenSubsKeyCommand = new RelayCommand(_ =>
        {
            var plain = Services.SecureKeyService.Decrypt(Settings.OpenSubtitlesApiKey);
            Settings.OpenSubtitlesApiKey = string.IsNullOrWhiteSpace(plain)
                ? string.Empty : plain;
            OpenSubsKeyLocked   = false;
            OpenSubsKeyValid    = null;
            Settings.OpenSubsKeyValidated = false;
            _ = App.ConfigService.SaveAsync();
            RaiseProperty(nameof(OpenSubsKeyDisplay));
        });

        SearchSubtitlesCommand = new AsyncRelayCommand(SearchSubtitlesAsync,
            _ => SelectedFile != null
              && !string.IsNullOrWhiteSpace(Settings.OpenSubtitlesApiKey)
              && !IsSearchingSubtitles);

        DownloadSubtitleCommand = new AsyncRelayCommand(DownloadSubtitleAsync,
            _ => SelectedSubtitleResult != null
              && SelectedFile != null
              && !IsSearchingSubtitles);

        ClearSubtitleResultsCommand = new RelayCommand(_ =>
        {
            SubtitleSearchResults.Clear();
            SelectedSubtitleResult = null;
            SubtitleDownloadStatus = string.Empty;
        });
    }

    private async Task ValidateOpenSubsKeyAsync()
    {
        var key = OpenSubsKey;
        if (string.IsNullOrWhiteSpace(key)) { OpenSubsKeyValid = false; return; }

        // Validate by searching for a well-known title — if we get results, key works
        var (results, err) = await _openSubsService.SearchAsync(
            key, imdbId: "0111161", languages: "en"); // The Shawshank Redemption

        if (err != null) { OpenSubsKeyValid = false; StatusText = $"OpenSubtitles: {err}"; return; }

        // Encrypt and lock
        Settings.OpenSubtitlesApiKey  = Services.SecureKeyService.Encrypt(key);
        Settings.OpenSubsKeyValidated = true;
        OpenSubsKeyLocked             = true;
        OpenSubsKeyValid              = true;
        _ = App.ConfigService.SaveAsync();
        RaiseProperty(nameof(OpenSubsKeyDisplay));
        StatusText = $"✓ OpenSubtitles key validated ({results.Count} results for test query).";
    }

    private async Task SearchSubtitlesAsync()
    {
        if (SelectedFile == null) return;
        IsSearchingSubtitles = true;
        SubtitleSearchResults.Clear();
        SelectedSubtitleResult = null;
        SubtitleDownloadStatus = string.Empty;

        var key   = OpenSubsKey;
        var file  = SelectedFile;
        var langs = SubtitleSearchLanguages.Trim();
        if (string.IsNullOrWhiteSpace(langs)) langs = "en";

        // Metadata is on EmbeddedMetadata (MovieMetadata), not VideoFile directly
        var meta = file.EmbeddedMetadata;

        StatusText = "OpenSubtitles: searching…";

        // Compute file hash for accurate matching (hash search >> title search)
        var (fileHash, fileSize) = Services.OpenSubtitlesService.ComputeHash(file.FilePath);
        if (!string.IsNullOrWhiteSpace(fileHash))
            Log(
                $"[{DateTime.Now:HH:mm:ss}] OpenSubtitles: using file hash search ({fileHash[..8]}…)");
        else
            Log(
                $"[{DateTime.Now:HH:mm:ss}] OpenSubtitles: using title/IMDb search (file too small for hash)");

        var (results, err) = await _openSubsService.SearchAsync(
            apiKey:    key,
            imdbId:    meta?.ImdbId,
            title:     meta?.Title ?? System.IO.Path.GetFileNameWithoutExtension(file.FileName),
            year:      meta?.Year,
            languages: langs,
            isEpisode: meta?.IsEpisode ?? false,
            season:    meta?.IsEpisode == true ? meta.Season : null,
            episode:   meta?.IsEpisode == true ? meta.Episode : null,
            fileHash:  fileHash,
            fileSize:  fileSize
        );

        IsSearchingSubtitles = false;

        if (err != null) { StatusText = $"OpenSubtitles error: {err}"; return; }
        if (results.Count == 0) { StatusText = "No subtitles found. Try different languages or check the IMDb ID."; return; }

        foreach (var r in results) SubtitleSearchResults.Add(r);
        var titleLabel = meta?.Title ?? file.FileName;
        StatusText = $"Found {results.Count} subtitle(s). Select one and click Download.";
        Log( $"[{DateTime.Now:HH:mm:ss}] OpenSubtitles: {results.Count} result(s) for '{titleLabel}'");
    }

    private async Task DownloadSubtitleAsync()
    {
        if (SelectedFile == null || SelectedSubtitleResult == null) return;

        var result = SelectedSubtitleResult;
        var file   = SelectedFile;
        var dir    = System.IO.Path.GetDirectoryName(file.FilePath) ?? string.Empty;
        var baseName = System.IO.Path.GetFileNameWithoutExtension(file.FilePath);

        IsSearchingSubtitles   = true;
        SubtitleDownloadStatus = "Downloading…";
        StatusText             = $"Downloading {result.LanguageName} subtitle…";

        var (savedPath, err) = await _openSubsService.DownloadAsync(
            apiKey:            OpenSubsKey,
            fileId:            result.FileId,
            destinationFolder: dir,
            videoBaseFileName: baseName,
            languageCode:      result.LanguageCode,
            format:            result.Format,
            isForced:          result.ForeignPartsOnly,
            isHearingImpaired: result.HearingImpaired
        );

        IsSearchingSubtitles = false;

        if (err != null)
        {
            SubtitleDownloadStatus = $"✗ {err}";
            StatusText = $"Subtitle download failed: {err}";
            return;
        }

        var savedName = System.IO.Path.GetFileName(savedPath);
        SubtitleDownloadStatus = $"✓ Saved: {savedName}";
        StatusText = $"✓ Subtitle saved: {savedName}";
        Log(
            $"[{DateTime.Now:HH:mm:ss}] ✓ Subtitle downloaded: {savedName}");

        // Refresh detected subtitles on the current file
        file.Subtitles = Services.SubtitleDetector.Detect(file.FilePath).ToList();
        RaiseProperty(nameof(SelectedFile));

        // Also update the matching LibraryEntry so the Subtitles column in the
        // Library tab reflects the download without requiring a full rescan.
        var entry = LibraryEntries.FirstOrDefault(e =>
            e.FilePath.Equals(file.FilePath, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            entry.Subtitles = Services.SubtitleDetector.Detect(file.FilePath).ToList();
            // Evict the stale cache entry so the next scan reads fresh from disk
            _libraryCacheService.Evict(file.FilePath);
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // TRAKT.TV
    // ══════════════════════════════════════════════════════════════════════════

    private readonly Services.TraktApiService _traktService = new();
    public  CancellationTokenSource? _traktDeviceCts;

    // ── Key fields — encrypted in config.json via DPAPI ───────────────────────
    private string TraktClientId     => Services.SecureKeyService.Decrypt(Settings.TraktClientId);
    private string TraktClientSecret => Services.SecureKeyService.Decrypt(Settings.TraktClientSecret);
    private string TraktAccessToken  => Settings.TraktAccessToken;  // already DPAPI-encrypted

    /// <summary>Displayed in the TextBox — masked when locked so encrypted hash is never shown.</summary>
    public string TraktClientIdDisplay
    {
        get => _traktClientIdLocked ? "●●●●●●●●●●●●●●●●●●●●  (saved)" : Settings.TraktClientId;
        set { if (!_traktClientIdLocked) Settings.TraktClientId = value; }
    }

    // ── UI state ──────────────────────────────────────────────────────────────
    private bool _traktClientIdLocked;
    public bool TraktClientIdLocked
    {
        get => _traktClientIdLocked;
        set => Set(ref _traktClientIdLocked, value);
    }

    private bool _traktClientSecretLocked;
    public bool TraktClientSecretLocked
    {
        get => _traktClientSecretLocked;
        set => Set(ref _traktClientSecretLocked, value);
    }

    private bool? _traktClientIdValid;
    public bool? TraktClientIdValid { get => _traktClientIdValid; set => Set(ref _traktClientIdValid, value); }

    private bool _traktConnecting;
    public bool TraktConnecting { get => _traktConnecting; set => Set(ref _traktConnecting, value); }

    private string _traktDeviceCode = string.Empty;
    public string TraktDeviceCode { get => _traktDeviceCode; set => Set(ref _traktDeviceCode, value); }

    private string _traktPinDisplay = string.Empty;
    public string TraktPinDisplay { get => _traktPinDisplay; set => Set(ref _traktPinDisplay, value); }

    public bool TraktConnected => Settings.TraktConnected;

    public string TraktStatusText => Settings.TraktConnected
        ? $"✓  Connected as @{Settings.TraktUsername}"
        : "Not connected";

    public string TraktLastSyncText => string.IsNullOrWhiteSpace(Settings.TraktLastSync)
        ? "Last sync: never"
        : $"Last sync: {Settings.TraktLastSync}";

    public string TraktTokenExpiryText
    {
        get
        {
            if (!Settings.TraktConnected || Settings.TraktTokenExpiry == 0)
                return string.Empty;
            var expiry  = DateTimeOffset.FromUnixTimeSeconds(Settings.TraktTokenExpiry);
            var daysLeft = (expiry - DateTimeOffset.UtcNow).TotalDays;
            if (daysLeft < 0)  return "⚠ Token expired — reconnect in Settings";
            if (daysLeft < 7)  return $"⚠ Token expires in {(int)daysLeft} day(s) — will auto-refresh on next sync";
            return $"Token valid until {expiry.LocalDateTime:yyyy-MM-dd}";
        }
    }

    public bool TraktSyncOnMark
    {
        get => Settings.TraktSyncOnMark;
        set { Settings.TraktSyncOnMark = value; RaiseProperty(); _ = App.ConfigService.SaveAsync(); }
    }

    public bool TraktSyncOnScan
    {
        get => Settings.TraktSyncOnScan;
        set { Settings.TraktSyncOnScan = value; RaiseProperty(); _ = App.ConfigService.SaveAsync(); }
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    public ICommand TraktConnectCommand     { get; private set; } = null!;
    public ICommand TraktDisconnectCommand  { get; private set; } = null!;
    public ICommand TraktSyncNowCommand     { get; private set; } = null!;
    public ICommand TraktCancelConnectCommand { get; private set; } = null!;
    public ICommand UnlockTraktClientIdCommand { get; private set; } = null!;
    public ICommand UnlockTraktSecretCommand   { get; private set; } = null!;

    private void InitTraktCommands()
    {
        TraktConnectCommand = new AsyncRelayCommand(TraktConnectAsync,
            _ => !TraktConnecting && !Settings.TraktConnected
              && !string.IsNullOrWhiteSpace(Settings.TraktClientId)
              && !string.IsNullOrWhiteSpace(Settings.TraktClientSecret));

        TraktDisconnectCommand = new RelayCommand(_ =>
        {
            _traktDeviceCts?.Cancel();
            Settings.TraktConnected    = false;
            Settings.TraktAccessToken  = string.Empty;
            Settings.TraktRefreshToken = string.Empty;
            Settings.TraktUsername     = string.Empty;
            Settings.TraktTokenExpiry  = 0;
            TraktConnecting            = false;
            TraktPinDisplay            = string.Empty;

            // Decrypt keys back to plain text so the fields show the editable values
            // This allows reconnect without needing to click the pencil icon
            var plainId     = Services.SecureKeyService.Decrypt(Settings.TraktClientId);
            var plainSecret = Services.SecureKeyService.Decrypt(Settings.TraktClientSecret);
            // Client ID shown in editable TextBox after unlock
            Settings.TraktClientId     = string.IsNullOrWhiteSpace(plainId) ? string.Empty : plainId;
            // Secret cleared — PasswordBox can't show restored values; user re-enters it
            Settings.TraktClientSecret = string.Empty;

            TraktClientIdLocked     = false;
            TraktClientSecretLocked = false;

            RaiseProperty(nameof(TraktConnected));
            RaiseProperty(nameof(TraktStatusText));
            RaiseProperty(nameof(Settings));
            _ = App.ConfigService.SaveAsync();
            StatusText = "Trakt.tv disconnected. Re-enter your keys to reconnect.";
        });

        TraktCancelConnectCommand = new RelayCommand(_ =>
        {
            _traktDeviceCts?.Cancel();
            TraktConnecting = false;
            TraktPinDisplay = string.Empty;
            StatusText = "Trakt connection cancelled.";
        });

        TraktSyncNowCommand = new AsyncRelayCommand(TraktSyncNowAsync,
            _ => Settings.TraktConnected && !IsBusy);

        UnlockTraktClientIdCommand = new RelayCommand(_ =>
        {
            // Decrypt back to plain text so the TextBox shows the editable key
            var plain = Services.SecureKeyService.Decrypt(Settings.TraktClientId);
            Settings.TraktClientId = string.IsNullOrWhiteSpace(plain)
                ? string.Empty : plain;
            TraktClientIdLocked = false;
            RaiseProperty(nameof(TraktClientIdDisplay));
        });

        UnlockTraktSecretCommand = new RelayCommand(_ =>
        {
            var plain = Services.SecureKeyService.Decrypt(Settings.TraktClientSecret);
            Settings.TraktClientSecret = string.IsNullOrWhiteSpace(plain)
                ? string.Empty : plain;
            TraktClientSecretLocked = false;
            RaiseProperty(nameof(Settings));
        });
    }

    private async Task TraktConnectAsync()
    {
        _traktDeviceCts = new CancellationTokenSource();
        var ct = _traktDeviceCts.Token;

        TraktConnecting = true;
        TraktPinDisplay = string.Empty;
        StatusText = "Trakt: requesting device code…";

        // Always decrypt — handles both first-use (plain text passes through)
        // and reconnect-after-disconnect (DPAPI-encrypted value gets decrypted)
        var clientId = Services.SecureKeyService.Decrypt(Settings.TraktClientId).Trim();
        var secret   = Services.SecureKeyService.Decrypt(Settings.TraktClientSecret).Trim();

        if (string.IsNullOrWhiteSpace(clientId))
        {
            TraktConnecting = false;
            StatusText = "Trakt: Client ID is empty. Enter your Client ID and try again.";
            return;
        }

        // Log masked client ID for debugging
        var maskedId = clientId.Length > 8
            ? clientId[..8] + "…" + $"({clientId.Length} chars)"
            : $"({clientId.Length} chars - too short)";
        Log( $"[{DateTime.Now:HH:mm:ss}] 🎯 Trakt: requesting device code (Client ID: {maskedId})");

        var (codeResult, codeErr) = await _traktService.RequestDeviceCodeAsync(clientId, ct);
        if (codeResult == null)
        {
            TraktConnecting = false;
            Log( $"[{DateTime.Now:HH:mm:ss}] 🎯 Trakt error: {codeErr}");
            StatusText = $"Trakt error: {codeErr}";
            return;
        }

        TraktPinDisplay = codeResult.UserCode;
        StatusText = $"Trakt: go to {codeResult.VerificationUrl} and enter code {codeResult.UserCode}";
        Log(
            $"[{DateTime.Now:HH:mm:ss}] 🎯 Trakt: visit {codeResult.VerificationUrl} and enter {codeResult.UserCode}");

        var progress = new System.Progress<string>(msg => StatusText = $"Trakt: {msg}");
        var (token, tokenErr) = await _traktService.PollForTokenAsync(
            clientId, secret, codeResult.DeviceCode, codeResult.Interval, progress, ct);

        TraktConnecting = false;
        TraktPinDisplay = string.Empty;

        if (token == null)
        {
            StatusText = $"Trakt connection failed: {tokenErr}";
            return;
        }

        // Store encrypted tokens
        Settings.TraktAccessToken  = token.AccessToken;
        Settings.TraktRefreshToken = token.RefreshToken;
        Settings.TraktTokenExpiry  = token.ExpiresAt;

        // Get username
        var (username, _) = await _traktService.GetUsernameAsync(clientId, token.AccessToken, ct);
        Settings.TraktUsername  = username ?? "unknown";
        Settings.TraktConnected = true;

        // Encrypt and persist both keys — same pattern as TMDB/OMDB
        Settings.TraktClientId     = Services.SecureKeyService.Encrypt(clientId);
        Settings.TraktClientSecret = Services.SecureKeyService.Encrypt(secret);
        TraktClientIdLocked        = true;
        TraktClientSecretLocked    = true;
        RaiseProperty(nameof(TraktClientIdDisplay));

        _ = App.ConfigService.SaveAsync();
        RaiseProperty(nameof(TraktConnected));
        RaiseProperty(nameof(TraktStatusText));
        RaiseProperty(nameof(TraktLastSyncText));
        StatusText = $"✓ Trakt connected as @{Settings.TraktUsername}";
        Log(
            $"[{DateTime.Now:HH:mm:ss}] ✓ Trakt.tv connected as @{Settings.TraktUsername}");
    }

    /// <summary>
    /// Checks token expiry and silently refreshes if within 7 days or expired.
    /// Returns false if refresh failed and the caller should abort.
    /// </summary>
    /// <summary>Prevents two concurrent Trakt token refreshes from consuming the refresh token.</summary>
    private readonly SemaphoreSlim _traktRefreshLock = new(1, 1);

    public async Task<bool> EnsureTraktTokenValidAsync()
    {
        if (!Settings.TraktConnected) return false;

        var now        = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sevenDays  = 7 * 24 * 3600L;
        var isExpiring = Settings.TraktTokenExpiry > 0
                      && Settings.TraktTokenExpiry - now < sevenDays;

        if (!isExpiring) return true;

        // Semaphore prevents two concurrent calls from both attempting a refresh.
        // The second caller waits for the first, then re-checks isExpiring —
        // if the first already refreshed successfully, the second returns true immediately.
        await _traktRefreshLock.WaitAsync();
        try
        {
            // Re-check inside the lock — another caller may have already refreshed
            var nowInner       = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var isExpiringStill = Settings.TraktTokenExpiry > 0
                               && Settings.TraktTokenExpiry - nowInner < sevenDays;
            if (!isExpiringStill) return true;

            Log($"[{DateTime.Now:HH:mm:ss}] 🎯 Trakt: access token expiring soon — refreshing…");

            var (newToken, err) = await _traktService.RefreshAccessTokenAsync(
                TraktClientId,
                Services.SecureKeyService.Decrypt(Settings.TraktClientSecret),
                Settings.TraktRefreshToken
            );

            if (newToken == null)
            {
                Log($"[{DateTime.Now:HH:mm:ss}] ⚠ Trakt token refresh failed: {err}. Please reconnect.");
                StatusText = "Trakt: token expired — go to Settings → Trakt.tv and reconnect.";
                return false;
            }

            Settings.TraktAccessToken  = newToken.AccessToken;
            Settings.TraktRefreshToken = newToken.RefreshToken;
            Settings.TraktTokenExpiry  = newToken.ExpiresAt;
            _ = App.ConfigService.SaveAsync();
            Log($"[{DateTime.Now:HH:mm:ss}] ✓ Trakt token refreshed.");
            return true;
        }
        finally
        {
            _traktRefreshLock.Release();
        }
    }

    public async Task TraktSyncNowAsync()
    {
        if (!Settings.TraktConnected) return;
        if (!await EnsureTraktTokenValidAsync()) return;
        IsBusy = true;
        StatusText = "Trakt: syncing watched history…";

        var clientId    = TraktClientId;
        var accessToken = TraktAccessToken;

        var (items, err) = await _traktService.GetWatchedHistoryAsync(clientId, accessToken);
        if (items == null)
        {
            StatusText = $"Trakt sync failed: {err}";
            IsBusy = false;
            return;
        }

        int matched = 0;
        foreach (var entry in LibraryEntries)
        {
            var match = items.FirstOrDefault(i =>
                (!string.IsNullOrWhiteSpace(entry.ImdbId) && entry.ImdbId == i.ImdbId) ||
                (!string.IsNullOrWhiteSpace(entry.TmdbId) && entry.TmdbId == i.TmdbId));

            if (match == null) continue;

            if (match.IsEpisode && entry.IsEpisode)
            {
                if (match.Season == entry.Season && match.Episode == entry.Episode)
                {
                    entry.IsWatched = true;
                    matched++;
                }
            }
            else if (!match.IsEpisode && !entry.IsEpisode)
            {
                entry.IsWatched = true;
                matched++;
            }
        }

        Settings.TraktLastSync = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        _ = App.ConfigService.SaveAsync();
        RaiseProperty(nameof(TraktLastSyncText));

        // Also update watched state on any matching files currently in the FILES panel
        // so the ♥ icon reflects Trakt history without requiring a library rescan.
        foreach (var vf in Files.Where(f => !f.IsSeparator))
        {
            var match = items.FirstOrDefault(i =>
                (!string.IsNullOrWhiteSpace(vf.EmbeddedMetadata.ImdbId) && vf.EmbeddedMetadata.ImdbId == i.ImdbId) ||
                (!string.IsNullOrWhiteSpace(vf.EmbeddedMetadata.TmdbId) && vf.EmbeddedMetadata.TmdbId == i.TmdbId));
            if (match == null) continue;
            bool matches = match.IsEpisode && vf.EmbeddedMetadata.IsEpisode
                ? match.Season == vf.EmbeddedMetadata.Season && match.Episode == vf.EmbeddedMetadata.Episode
                : !match.IsEpisode && !vf.EmbeddedMetadata.IsEpisode;
            if (matches) vf.IsWatched = true;
        }
        RaiseProperty(nameof(TvShowTree));

        IsBusy = false;
        StatusText = $"✓ Trakt sync complete — {matched} items marked watched";
        Log(
            $"[{DateTime.Now:HH:mm:ss}] ✓ Trakt sync: {matched} library items marked watched");
    }

    /// <summary>Called when user marks a file watched — pushes to Trakt if connected.</summary>
    public async Task TraktPushWatchedAsync(LibraryEntry entry)
    {
        if (!Settings.TraktConnected || !Settings.TraktSyncOnMark) return;
        if (!await EnsureTraktTokenValidAsync()) return;
        var (ok, err) = await _traktService.MarkWatchedAsync(
            TraktClientId, TraktAccessToken,
            entry.ImdbId, entry.TmdbId,
            entry.IsEpisode, entry.Season, entry.Episode);
        if (!ok)
            Log( $"[{DateTime.Now:HH:mm:ss}] ⚠ Trakt push failed: {err}");
    }


}
