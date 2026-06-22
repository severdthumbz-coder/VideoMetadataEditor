using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;

namespace VideoMetadataEditor.ViewModels;

/// <summary>
/// Self-contained ViewModel for the Duplicates tab.
/// Decoupled from MainViewModel — communicates back only via events.
/// </summary>
public enum SmartSelectMode
{
    None,
    Smaller,    // smaller of each pair
    Larger,     // larger of each pair
    Untagged,   // file with no embedded title
    InUnsorted, // file located in any \Unsorted\ folder
}

public enum AutoResolveRule
{
    KeepLargest,   // selects smaller files for deletion
    KeepNewest,    // selects older files for deletion
    KeepTagged,    // selects untagged files for deletion
}

public class DuplicatesViewModel : ViewModelBase
{
    private readonly MetadataService _metaSvc;
    private readonly Services.FingerprintCacheService _fpCache = new();
    private CancellationTokenSource? _deepScanCts;

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Raised when a file is successfully deleted so callers can log it.</summary>
    public event EventHandler<string>? FileDeleted;

    /// <summary>Raised when an error occurs (delete failure, scan error, etc.).</summary>
    public event EventHandler<string>? ErrorOccurred;

    // ── Commands ──────────────────────────────────────────────────────────────

    public ICommand BrowseSourceCommand      { get; }
    public ICommand BrowseDestinationCommand { get; }
    public ICommand ClearSourceCommand       { get; }
    public ICommand ClearDestinationCommand  { get; }
    public ICommand ScanCommand              { get; }
    public ICommand CancelScanCommand        { get; }
    public ICommand DeleteSelectedCommand    { get; }
    public ICommand MoveSelectedCommand      { get; }
    public ICommand SelectOldestCommand      { get; }
    public ICommand SelectNewestCommand      { get; }

    // Smart selection — applies to ALL groups, not just selected
    public ICommand  SelectSmallerEverywhereCommand { get; private set; } = null!;
    public ICommand  SelectLargerEverywhereCommand  { get; private set; } = null!;
    public ICommand  SelectUntaggedEverywhereCommand { get; private set; } = null!;
    public ICommand  SelectInUnsortedCommand        { get; private set; } = null!;
    public ICommand  ClearAllSelectionsCommand      { get; private set; } = null!;

    // Auto-resolve rules — picks "loser" per group automatically
    public ICommand  AutoResolveKeepLargestCommand  { get; private set; } = null!;
    public ICommand  AutoResolveKeepNewestCommand   { get; private set; } = null!;
    public ICommand  AutoResolveKeepTaggedCommand   { get; private set; } = null!;

    // Bulk delete based on IsSelected across all groups (Smart Select workflow)
    public ICommand  DeleteAllSelectedCommand       { get; private set; } = null!;

    // Reference scan + fingerprint cache management
    public ICommand  FindByReferenceFileCommand     { get; private set; } = null!;
    public ICommand  ExportFingerprintCacheCommand  { get; private set; } = null!;
    public ICommand  ImportFingerprintCacheCommand  { get; private set; } = null!;

    // ── Side-by-side preview ──────────────────────────────────────────────────
    public ICommand? PreviewGroupCommand     { get; private set; }
    public ICommand  DeepScanCommand         { get; private set; } = null!;
    public ICommand  CancelDeepScanCommand   { get; private set; } = null!;
    public ICommand PlayBothCommand          { get; } = new RelayCommand(_ => { });
    public ICommand StopBothCommand          { get; } = new RelayCommand(_ => { });
    public ICommand DeleteLeftCommand        { get; } = new RelayCommand(_ => { });
    public ICommand DeleteRightCommand       { get; } = new RelayCommand(_ => { });
    public ICommand SwapPreviewCommand       { get; } = new RelayCommand(_ => { });

    // ── Cancellation ──────────────────────────────────────────────────────────

    private CancellationTokenSource? _cts;

    // ── Source / Destination ──────────────────────────────────────────────────

    private bool _includeSubfolders = true;
    public bool IncludeSubfolders
    {
        get => _includeSubfolders;
        set { Set(ref _includeSubfolders, value); App.ConfigService.Settings.DuplicateIncludeSubfolders = value; _ = App.ConfigService.SaveAsync(); }
    }

    private string _sourceFolder = string.Empty;
    public string SourceFolder
    {
        get => _sourceFolder;
        set
        {
            Set(ref _sourceFolder, value);
            App.ConfigService.Settings.DuplicateSourceFolder = value;
            _ = App.ConfigService.SaveAsync();
            RaiseProperty(nameof(CanScan));
        }
    }

    private string _destinationFolder = string.Empty;
    public string DestinationFolder
    {
        get => _destinationFolder;
        set
        {
            Set(ref _destinationFolder, value);
            App.ConfigService.Settings.DuplicateDestinationFolder = value;
            _ = App.ConfigService.SaveAsync();
            RaiseProperty(nameof(CanMoveSelected));
        }
    }

    private bool _moveInsteadOfDelete;
    public bool MoveInsteadOfDelete
    {
        get => _moveInsteadOfDelete;
        set
        {
            Set(ref _moveInsteadOfDelete, value);
            App.ConfigService.Settings.DuplicateMoveInsteadOfDelete = value;
            _ = App.ConfigService.SaveAsync();
            RaiseProperty(nameof(DeleteButtonLabel));
        }
    }

    // ── Scan state ────────────────────────────────────────────────────────────

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        set { Set(ref _isScanning, value); RaiseProperty(nameof(CanScan)); RaiseProperty(nameof(IsIdle)); }
    }

    public bool IsIdle => !_isScanning;

    private int _scanProgress;
    public int ScanProgress { get => _scanProgress; set => Set(ref _scanProgress, value); }

    private string _scanStatus = "Select a Source Folder and click Scan.";
    public string ScanStatus { get => _scanStatus; set => Set(ref _scanStatus, value); }

    // ── Side-by-side preview state ────────────────────────────────────────────
    private DuplicateFileViewModel? _previewLeft;
    public DuplicateFileViewModel? PreviewLeft
    {
        get => _previewLeft;
        set { Set(ref _previewLeft, value); RaiseProperty(nameof(HasPreviewPair)); }
    }

    private DuplicateFileViewModel? _previewRight;
    public DuplicateFileViewModel? PreviewRight
    {
        get => _previewRight;
        set { Set(ref _previewRight, value); RaiseProperty(nameof(HasPreviewPair)); }
    }

    public bool HasPreviewPair => PreviewLeft != null && PreviewRight != null;

    private bool _previewIsPlaying;
    public bool PreviewIsPlaying
    {
        get => _previewIsPlaying;
        set => Set(ref _previewIsPlaying, value);
    }

    private string _previewStatusLeft  = string.Empty;
    public string PreviewStatusLeft  { get => _previewStatusLeft;  set => Set(ref _previewStatusLeft, value); }

    private string _previewStatusRight = string.Empty;
    public string PreviewStatusRight { get => _previewStatusRight; set => Set(ref _previewStatusRight, value); }

    // ── Results ───────────────────────────────────────────────────────────────

    private ObservableCollection<DuplicateGroupViewModel> _groups = [];
    public ObservableCollection<DuplicateGroupViewModel> Groups
    {
        get => _groups;
        set
        {
            // Unsubscribe from old groups
            if (_groups != null)
            {
                foreach (var g in _groups) g.FileSelectionChanged -= OnGroupFileSelectionChanged;
                _groups.CollectionChanged -= OnGroupsCollectionChanged;
            }

            Set(ref _groups, value);

            // Subscribe to all groups in the new collection
            if (_groups != null)
            {
                foreach (var g in _groups) g.FileSelectionChanged += OnGroupFileSelectionChanged;
                _groups.CollectionChanged += OnGroupsCollectionChanged;
            }

            RaiseProperty(nameof(HasGroups));
            RaiseProperty(nameof(HasNoGroups));
            RaiseProperty(nameof(GroupCountLabel));
            RaiseProperty(nameof(SelectedFileCount));
            RaiseProperty(nameof(SelectedFilesSize));
        }
    }

    private void OnGroupsCollectionChanged(object? sender,
        System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Newly added groups need a subscription; removed groups need cleanup
        if (e.NewItems != null)
            foreach (DuplicateGroupViewModel g in e.NewItems)
                g.FileSelectionChanged += OnGroupFileSelectionChanged;
        if (e.OldItems != null)
            foreach (DuplicateGroupViewModel g in e.OldItems)
                g.FileSelectionChanged -= OnGroupFileSelectionChanged;
        RaiseProperty(nameof(SelectedFileCount));
        RaiseProperty(nameof(SelectedFilesSize));
    }

    private void OnGroupFileSelectionChanged(object? sender, EventArgs e)
    {
        RaiseProperty(nameof(SelectedFileCount));
        RaiseProperty(nameof(SelectedFilesSize));
    }

    private DuplicateGroupViewModel? _selectedGroup;
    public DuplicateGroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            Set(ref _selectedGroup, value);
            SelectedFile = value?.Files.FirstOrDefault();
            RaiseProperty(nameof(HasSelectedGroup));
        }
    }

    public bool HasSelectedGroup => _selectedGroup != null;

    private DuplicateFileViewModel? _selectedFile;
    public DuplicateFileViewModel? SelectedFile
    {
        get => _selectedFile;
        set
        {
            Set(ref _selectedFile, value);
            RaiseProperty(nameof(HasSelectedFile));
            RaiseProperty(nameof(CanDeleteSelected));
            RaiseProperty(nameof(CanMoveSelected));
            RaiseProperty(nameof(DeleteButtonLabel));
        }
    }

    public bool HasSelectedFile   => _selectedFile != null;
    public bool CanScan           => !_isScanning && !string.IsNullOrWhiteSpace(_sourceFolder);
    public bool CanDeleteSelected => !_isScanning && _selectedFile != null;
    public bool CanMoveSelected   => !_isScanning && _selectedFile != null
                                  && !string.IsNullOrWhiteSpace(_destinationFolder);

    public string DeleteButtonLabel => _moveInsteadOfDelete ? "📦 Move to Destination" : "🗑 Delete File";

    /// <summary>
    /// Shows the active copy engine so the user knows which engine will be used
    /// — mirrors the selection on the Move/Copy tab.
    /// </summary>
    public string ActiveEngineLabel
    {
        get
        {
            var engine = FileCopyService.ResolvedEngine(
                App.ConfigService.Settings.PreferredCopyEngine);
            return engine switch
            {
                CopyEngine.FastCopy => "FastCopy",
                CopyEngine.BuiltIn  => "Custom Fast (built-in)",
                _                   => "Custom Fast (built-in)"
            };
        }
    }

    private int _totalGroups;
    private int _totalDuplicates;
    public string SummaryText => _totalGroups > 0
        ? $"{_totalGroups} group(s) · {_totalDuplicates} redundant file(s)"
        : string.Empty;

    public string ScanSummaryLine => _totalGroups > 0
        ? $"{_totalGroups} group(s)  ·  {_totalDuplicates} redundant file(s)  ·  {_totalFilesScanned} files scanned"
        : _totalFilesScanned > 0
            ? $"No duplicates found  ·  {_totalFilesScanned} files scanned"
            : string.Empty;

    public string GroupCountLabel => _totalGroups > 0 ? $"{_totalGroups}" : string.Empty;

    public bool HasGroups   => Groups.Count > 0;
    public bool HasNoGroups => Groups.Count == 0;

    public string EmptyGroupsHint => string.IsNullOrWhiteSpace(_sourceFolder)
        ? "Select a Source Folder and click Scan"
        : _totalFilesScanned > 0
            ? $"No duplicates found  ·  {_totalFilesScanned} file(s) scanned"
            : "Click Scan to start";

    private int _totalFilesScanned;

    // ── Deep Scan state ───────────────────────────────────────────────────────
    private bool   _isDeepScanning;
    public  bool   IsDeepScanning
    {
        get => _isDeepScanning;
        private set { _isDeepScanning = value; RaiseProperty(); }
    }

    private string _deepScanStatus = string.Empty;
    public  string DeepScanStatus
    {
        get => _deepScanStatus;
        private set { _deepScanStatus = value; RaiseProperty(); }
    }

    private int _deepScanProgress;
    public  int DeepScanProgress
    {
        get => _deepScanProgress;
        private set { _deepScanProgress = value; RaiseProperty(); }
    }

    private bool _chromaprintAvailable;
    public  bool ChromaprintAvailable
    {
        get => _chromaprintAvailable;
        private set { _chromaprintAvailable = value; RaiseProperty(); }
    }

    private string _fpcalcVersion = string.Empty;
    public  string FpcalcVersion
    {
        get => _fpcalcVersion;
        private set { _fpcalcVersion = value; RaiseProperty(); RaiseProperty(nameof(FpcalcStatusLabel)); }
    }

    private bool _isInstallingFpcalc;
    public  bool IsInstallingFpcalc
    {
        get => _isInstallingFpcalc;
        private set { _isInstallingFpcalc = value; RaiseProperty(); }
    }

    public string FpcalcStatusLabel => _chromaprintAvailable
        ? (string.IsNullOrEmpty(_fpcalcVersion) ? string.Empty : $"v{_fpcalcVersion}")
        : "fpcalc not installed";

    public ICommand? InstallFpcalcCommand { get; private set; }

    // ── Constructor ───────────────────────────────────────────────────────────

    private readonly Services.IDialogService _dialogs;
    private readonly Services.IUiDispatcher _ui;

    public DuplicatesViewModel(MetadataService metaSvc, Services.IDialogService? dialogs = null,
        Services.IUiDispatcher? ui = null)
    {
        _metaSvc = metaSvc;
        _dialogs = dialogs ?? Services.NullDialogService.Instance;
        _ui      = ui ?? Services.NullUiDispatcher.Instance;

        // Restore last-used folders from settings
        _sourceFolder      = App.ConfigService.Settings.DuplicateSourceFolder;
        _destinationFolder = App.ConfigService.Settings.DuplicateDestinationFolder;
        _moveInsteadOfDelete = App.ConfigService.Settings.DuplicateMoveInsteadOfDelete;

        BrowseSourceCommand      = new RelayCommand(_ => BrowseSource());
        BrowseDestinationCommand = new RelayCommand(_ => BrowseDestination());
        ClearSourceCommand       = new RelayCommand(_ => SourceFolder      = string.Empty);
        ClearDestinationCommand  = new RelayCommand(_ => DestinationFolder = string.Empty);
        ScanCommand              = new AsyncRelayCommand(ScanAsync, _ => CanScan);
        CancelScanCommand        = new RelayCommand(_ => _cts?.Cancel(), _ => _isScanning);
        DeleteSelectedCommand    = new AsyncRelayCommand(DeleteOrMoveAsync,
            _ => CanDeleteSelected || CanMoveSelected);
        MoveSelectedCommand      = new AsyncRelayCommand(DeleteOrMoveAsync, _ => CanMoveSelected);
        SelectOldestCommand      = new RelayCommand(_ => SelectByDate(oldest: true),
            _ => _selectedGroup?.Files.Count > 1);
        SelectNewestCommand      = new RelayCommand(_ => SelectByDate(oldest: false),
            _ => _selectedGroup?.Files.Count > 1);
        PreviewGroupCommand = new RelayCommand(
            _ => OpenCompareDialog(SelectedGroup),
            _ => SelectedGroup?.Files.Count >= 2);

        DeepScanCommand     = new AsyncRelayCommand(RunDeepScanAsync,
            _ => !IsDeepScanning && !IsScanning);
        CancelDeepScanCommand = new RelayCommand(_ =>
        {
            _deepScanCts?.Cancel();
            IsDeepScanning = false;
            DeepScanStatus = "Cancelled.";
        }, _ => IsDeepScanning);
        InstallFpcalcCommand = new AsyncRelayCommand(RunInstallFpcalcAsync,
            _ => !IsInstallingFpcalc && !IsDeepScanning);

        // Smart selection (#8) — operates across all groups at once
        SelectSmallerEverywhereCommand  = new RelayCommand(_ => SmartSelect(SmartSelectMode.Smaller),
            _ => HasGroups);
        SelectLargerEverywhereCommand   = new RelayCommand(_ => SmartSelect(SmartSelectMode.Larger),
            _ => HasGroups);
        SelectUntaggedEverywhereCommand = new RelayCommand(_ => SmartSelect(SmartSelectMode.Untagged),
            _ => HasGroups);
        SelectInUnsortedCommand         = new RelayCommand(_ => SmartSelect(SmartSelectMode.InUnsorted),
            _ => HasGroups);
        ClearAllSelectionsCommand       = new RelayCommand(_ => SmartSelect(SmartSelectMode.None),
            _ => HasGroups);

        // Auto-resolve (#4) — picks losers per rule, then user can review/delete
        AutoResolveKeepLargestCommand   = new RelayCommand(_ => AutoResolve(AutoResolveRule.KeepLargest),
            _ => HasGroups);
        AutoResolveKeepNewestCommand    = new RelayCommand(_ => AutoResolve(AutoResolveRule.KeepNewest),
            _ => HasGroups);
        AutoResolveKeepTaggedCommand    = new RelayCommand(_ => AutoResolve(AutoResolveRule.KeepTagged),
            _ => HasGroups);

        // Bulk delete — operates on IsSelected=true across all groups
        DeleteAllSelectedCommand = new AsyncRelayCommand(DeleteAllSelectedAsync,
            _ => SelectedFileCount > 0);

        // Reference scan + cache management
        FindByReferenceFileCommand     = new AsyncRelayCommand(RunReferenceScanAsync,
            _ => ChromaprintAvailable && !IsDeepScanning && !IsScanning);
        ExportFingerprintCacheCommand  = new RelayCommand(_ => ExportFingerprintCache());
        ImportFingerprintCacheCommand  = new RelayCommand(_ => ImportFingerprintCache());

        _includeSubfolders = App.ConfigService.Settings.DuplicateIncludeSubfolders;

        // Check native library availability after startup
        Services.BackgroundTask.Run(async () =>
        {
            Services.NativeLibraryExtractor.EnsureExtracted();
            ChromaprintAvailable = Services.NativeLibraryExtractor.IsFpcalcAvailable();
            if (ChromaprintAvailable)
                FpcalcVersion = await Services.FpcalcInstallerService.GetInstalledVersionAsync()
                                ?? string.Empty;
        }, "fpcalc detection");
    }

    // ── Select by date ───────────────────────────────────────────────────────

    private void SelectByDate(bool oldest)
    {
        if (_selectedGroup == null) return;
        var files = _selectedGroup.Files;
        if (files.Count == 0) return;
        // Parse the CreatedDisplay back to compare — or use FileInfo directly
        var target = oldest
            ? files.OrderBy(f => f.CreatedRaw).FirstOrDefault()
            : files.OrderByDescending(f => f.CreatedRaw).FirstOrDefault();
        if (target != null) SelectedFile = target;
    }

    // ── Folder Browse ─────────────────────────────────────────────────────────

    private void BrowseSource()
    {
        var picked = _dialogs.PickFolder("Select Source Folder to scan for duplicates", _sourceFolder);
        if (picked != null) SourceFolder = picked;
    }

    private void BrowseDestination()
    {
        var picked = _dialogs.PickFolder("Select Destination Folder (for moving duplicates)", _destinationFolder);
        if (picked != null) DestinationFolder = picked;
    }

    // ── Scan ─────────────────────────────────────────────────────────────────

    private async Task ScanAsync()
    {
        if (!Directory.Exists(_sourceFolder))
        {
            ScanStatus = "Source folder does not exist.";
            return;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        IsScanning  = true;
        ScanProgress = 0;
        Groups       = [];
        SelectedGroup = null;
        SelectedFile  = null;
        _totalGroups = 0;
        _totalDuplicates = 0;
        _totalFilesScanned = 0;
        RaiseProperty(nameof(SummaryText));
        RaiseProperty(nameof(ScanSummaryLine));
        RaiseProperty(nameof(GroupCountLabel));
        RaiseProperty(nameof(HasGroups));
        RaiseProperty(nameof(HasNoGroups));
        RaiseProperty(nameof(EmptyGroupsHint));
        ScanStatus = "Scanning folder for video files…";

        try
        {
            // ── Phase 1: collect video files ────────────────────────────────
            var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".mp4", ".mkv", ".mov", ".wmv", ".m4v", ".webm" };

            var searchOpt = _includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var paths = await Task.Run(() =>
                Directory.EnumerateFiles(_sourceFolder, "*.*", searchOpt)
                .Concat(
                    !string.IsNullOrWhiteSpace(_destinationFolder)
                    && Directory.Exists(_destinationFolder)
                    && !_sourceFolder.Equals(_destinationFolder, StringComparison.OrdinalIgnoreCase)
                        ? Directory.EnumerateFiles(_destinationFolder, "*.*", searchOpt)
                        : Enumerable.Empty<string>())
                .Where(f => extensions.Contains(Path.GetExtension(f))
                         && !Path.GetFileName(f).StartsWith(".vme_", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(), ct);

            if (paths.Count == 0)
            {
                ScanStatus = "No video files found in selected folder(s).";
                return;
            }

            ScanStatus = $"Reading metadata from {paths.Count} file(s)…";

            // ── Phase 2: read metadata fast (no artwork) ────────────────────
            var videoFiles = new System.Collections.Concurrent.ConcurrentBag<VideoFile>();
            int read = 0;

            var sem = new SemaphoreSlim(
                Services.DriveCapabilityService.GetProfile(_sourceFolder).RecommendedMetadataWorkers);

            var readTasks = paths.Select(async path =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var format = MetadataService.DetectFormat(path);
                    var (art, full, warn) = MetadataService.GetFormatCapabilities(format);
                    var info     = new FileInfo(path);
                    var embedded = _metaSvc.ReadMetadataFast(path);
                    var (pt, py) = FilenameParser.Parse(Path.GetFileNameWithoutExtension(path));

                    if (!string.IsNullOrWhiteSpace(pt))
                        embedded.Title = !string.IsNullOrWhiteSpace(embedded.Title) ? embedded.Title : pt;
                    if (!string.IsNullOrWhiteSpace(py))
                        embedded.Year  = !string.IsNullOrWhiteSpace(embedded.Year)  ? embedded.Year  : py;

                    var vf = new VideoFile
                    {
                        FilePath         = path,
                        Format           = format,
                        FileSizeBytes    = info.Length,
                        SupportsArtwork  = art,
                        SupportsFullTags = full,
                        FormatWarning    = warn,
                        EmbeddedMetadata = embedded,
                        ParsedTitle      = pt,
                        ParsedYear       = py
                    };
                    vf.PendingMetadata = embedded.Clone();
                    videoFiles.Add(vf);

                    var done = System.Threading.Interlocked.Increment(ref read);
                    _ui.Post(() =>
                    {
                        ScanProgress = (int)((double)done / paths.Count * 50);
                        ScanStatus   = $"Reading metadata {done} / {paths.Count}…";
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Duplicates] {ex.GetType().Name}: {ex.Message}"); }
                finally { sem.Release(); }
            }).ToList();

            await Task.WhenAll(readTasks);
            ct.ThrowIfCancellationRequested();

            if (videoFiles.Count < 2)
            {
                ScanStatus = "Not enough video files to check for duplicates.";
                return;
            }

            // ── Phase 3: detect duplicates ───────────────────────────────────
            ScanStatus = "Detecting duplicates…";

            var progressReporter = new Progress<(string current, int done, int total)>(p =>
            {
                ScanProgress = 50 + (p.total > 0 ? (int)((double)p.done / p.total * 50) : 0);
                ScanStatus   = $"Hashing {p.done}/{p.total}: {p.current}";
            });

            var groups = await DuplicateDetectorService.DetectAsync(
                videoFiles, useHashing: true, progress: progressReporter, ct: ct);

            // ── Phase 4: build result view models ────────────────────────────
            // Deduplicate groups across stages: if two groups share the same
            // set of files (e.g. caught by both size-match and metadata-match),
            // keep the one with higher confidence and discard the other.
            var deduped = DeduplicateGroups(groups);
            var grouped = deduped
                .Select(g => new DuplicateGroupViewModel(g, _sourceFolder, _destinationFolder))
                .ToList();

            Groups       = new ObservableCollection<DuplicateGroupViewModel>(grouped);
            SelectedGroup = Groups.FirstOrDefault();

            _totalGroups       = Groups.Count;
            _totalDuplicates   = Groups.Sum(g => g.Files.Count - 1);
            _totalFilesScanned = videoFiles.Count;
            RaiseProperty(nameof(SummaryText));
            RaiseProperty(nameof(ScanSummaryLine));
            RaiseProperty(nameof(GroupCountLabel));
            RaiseProperty(nameof(HasGroups));
            RaiseProperty(nameof(HasNoGroups));
            RaiseProperty(nameof(EmptyGroupsHint));

            ScanProgress = 100;
            ScanStatus   = _totalGroups > 0
                ? $"Found {_totalGroups} group(s) with {_totalDuplicates} redundant file(s)."
                : "No duplicates found — all files are unique.";
        }
        catch (OperationCanceledException)
        {
            ScanStatus = "Scan cancelled.";
            ScanProgress = 0;
        }
        catch (Exception ex)
        {
            ScanStatus = $"Scan error: {ex.Message}";
            ErrorOccurred?.Invoke(this, ex.Message);
        }
        finally
        {
            IsScanning = false;
        }
    }

    // ── Delete / Move ─────────────────────────────────────────────────────────

    /// <summary>
    /// Batch-deletes (or moves) every file where IsSelected=true across all groups.
    /// Used by the Smart Select / Auto-Resolve workflow. Sends to Recycle Bin so
    /// nothing is permanently destroyed.
    /// </summary>
    private async Task DeleteAllSelectedAsync()
    {
        var selectedFiles = Groups
            .SelectMany(g => g.Files.Select(f => new { Group = g, File = f }))
            .Where(x => x.File.IsSelected)
            .ToList();

        if (selectedFiles.Count == 0) return;

        long totalBytes = selectedFiles.Sum(x => x.File.FileSizeBytes);
        var  sizeText   = SelectedFilesSize;

        bool doMove = _moveInsteadOfDelete && !string.IsNullOrWhiteSpace(_destinationFolder);
        string verb = doMove ? "move" : "delete";

        // Build preview of first few file names (capped to keep dialog readable)
        var preview = string.Join("\n",
            selectedFiles.Take(5).Select(x => $"  • {x.File.FileName}"));
        if (selectedFiles.Count > 5)
            preview += $"\n  …and {selectedFiles.Count - 5} more";

        var confirm = _dialogs.Show(
            $"You're about to {verb} {selectedFiles.Count} file(s)  ·  {sizeText}\n\n" +
            preview + "\n\n" +
            (doMove
                ? $"Files will be moved to:\n  {_destinationFolder}\n\nContinue?"
                : "Files will be moved to the Recycle Bin.\n\nContinue?"),
            doMove ? "Confirm Batch Move" : "Confirm Batch Delete",
            Services.DialogButtons.YesNo,
            Services.DialogIcon.Warning);

        if (confirm != Services.DialogResult.Yes) return;

        int succeeded = 0, failed = 0;
        var errors    = new List<string>();
        var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ScanStatus = $"Processing {selectedFiles.Count} file(s)…";

        foreach (var item in selectedFiles)
        {
            var path = item.File.FilePath;
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    // File already gone — treat as success since it's not in the way
                    succeeded++;
                    processed.Add(path);
                    continue;
                }

                if (doMove)
                {
                    System.IO.Directory.CreateDirectory(_destinationFolder);
                    var destPath = System.IO.Path.Combine(_destinationFolder,
                        System.IO.Path.GetFileName(path));
                    // Unique-name if collision
                    int n = 1;
                    while (System.IO.File.Exists(destPath))
                    {
                        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
                        var ext  = System.IO.Path.GetExtension(path);
                        destPath = System.IO.Path.Combine(_destinationFolder,
                            $"{stem} ({n++}){ext}");
                    }
                    System.IO.File.Move(path, destPath);
                }
                else
                {
                    // Recycle Bin via VisualBasic FileSystem.
                    // Network/UNC paths don't have a Recycle Bin — the VB method
                    // throws silently or performs a permanent delete. Detect and
                    // warn the user explicitly so they can confirm before data loss.
                    bool isNetwork = path.StartsWith("\\\\", StringComparison.Ordinal);
                    if (!isNetwork)
                    {
                        try
                        {
                            var root = System.IO.Path.GetPathRoot(path);
                            if (!string.IsNullOrWhiteSpace(root))
                            {
                                var di = new System.IO.DriveInfo(root);
                                isNetwork = di.DriveType == System.IO.DriveType.Network;
                            }
                        }
                        catch { /* DriveInfo unavailable — treat as local */ }
                    }

                    if (isNetwork)
                    {
                        // Recycle Bin unavailable on network paths — confirm permanent delete
                        var networkConfirm = _dialogs.Show(
                            $"'{System.IO.Path.GetFileName(path)}' is on a network path.\n\n" +
                            "Network files cannot be sent to the Recycle Bin — this will permanently delete the file.\n\n" +
                            "Delete permanently?",
                            "Network file — permanent delete",
                            Services.DialogButtons.YesNo,
                            Services.DialogIcon.Warning);
                        if (networkConfirm != Services.DialogResult.Yes)
                        {
                            failed++;
                            errors.Add($"{System.IO.Path.GetFileName(path)}: skipped (network path — permanent delete declined).");
                            continue;
                        }
                        System.IO.File.Delete(path);
                    }
                    else
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    }
                }
                succeeded++;
                processed.Add(path);
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{System.IO.Path.GetFileName(path)}: {ex.Message}");
            }
        }

        // Remove processed files from groups, drop groups with <2 files remaining
        var groupsToRemove = new List<DuplicateGroupViewModel>();
        foreach (var grp in Groups.ToList())
        {
            var stillThere = grp.Files.Where(f => !processed.Contains(f.FilePath)).ToList();
            if (stillThere.Count < 2)
                groupsToRemove.Add(grp);
            else if (stillThere.Count < grp.Files.Count)
            {
                // Rebuild group with surviving files
                var idx = Groups.IndexOf(grp);
                var newGrp = new DuplicateGroupViewModel(
                    new Services.DuplicateGroup(
                        stillThere.Select(fvm => new VideoFile
                        {
                            FilePath      = fvm.FilePath,
                            FileSizeBytes = fvm.FileSizeBytes,
                        }).ToList(),
                        grp.Confidence,
                        grp.Reason),
                    SourceFolder ?? string.Empty,
                    DestinationFolder ?? string.Empty);
                Groups[idx] = newGrp;
            }
        }
        foreach (var rg in groupsToRemove) Groups.Remove(rg);

        // Status report
        var verb2 = doMove ? "moved" : "moved to Recycle Bin";
        ScanStatus = failed > 0
            ? $"⚠ {succeeded} file(s) {verb2}, {failed} failed. See log."
            : $"✓ {succeeded} file(s) {verb2}  ·  {sizeText} freed.";

        if (errors.Count > 0)
            _dialogs.Show(
                "Some files couldn't be processed:\n\n" + string.Join("\n", errors.Take(10)),
                "Batch operation — partial failure",
                Services.DialogButtons.Ok,
                Services.DialogIcon.Warning);

        RaiseProperty(nameof(HasGroups));
        RaiseProperty(nameof(HasNoGroups));
        RaiseProperty(nameof(SummaryText));
        RaiseProperty(nameof(SelectedFileCount));
        RaiseProperty(nameof(SelectedFilesSize));
    }

    private async Task DeleteOrMoveAsync()
    {
        if (_selectedFile == null || _selectedGroup == null) return;

        var file     = _selectedFile;
        var group    = _selectedGroup;
        var filePath = file.FilePath;
        var fileName = file.FileName;

        // Build confirmation message
        var settings = App.ConfigService.Settings;
        bool doMove  = _moveInsteadOfDelete && !string.IsNullOrWhiteSpace(_destinationFolder);
        string action = doMove ? $"move to {_destinationFolder}" : "permanently delete";

        var confirm = _dialogs.Show(
            $"Are you sure you want to {action} this file?\n\n" +
            $"  {fileName}\n" +
            $"  {file.FileSizeDisplay}  ·  {file.FolderPath}\n\n" +
            "This action cannot be undone.",
            doMove ? "Confirm Move" : "Confirm Delete",
            Services.DialogButtons.YesNo,
            Services.DialogIcon.Warning);

        if (confirm != Services.DialogResult.Yes) return;

        try
        {
            if (doMove)
            {
                // ── Use the same copy engine as the Move/Copy tab ─────────────
                Directory.CreateDirectory(_destinationFolder);
                var engine = FileCopyService.ResolvedEngine(settings.PreferredCopyEngine,
                    new[] { filePath });

                if (engine == CopyEngine.FastCopy)
                {
                    var result = await FileCopyService.RunExternalAsync(
                        new[] { filePath },
                        _destinationFolder,
                        CopyMode.Move,
                        CopyEngine.FastCopy,
                        ConflictMode.Rename);

                    if (!result.Success)
                        throw new IOException(result.ErrorMessage ?? "FastCopy move failed.");
                }
                else
                {
                    // Custom Fast (built-in) engine
                    var results = await FileCopyService.BuiltInBatchCopyAsync(
                        new[] { filePath },
                        _destinationFolder,
                        CopyMode.Move,
                        ConflictMode.Rename);

                    var failed = results.FirstOrDefault(r => !r.Success);
                    if (failed != null)
                        throw new IOException(failed.Error ?? "Move failed.");
                }

                FileDeleted?.Invoke(this,
                    $"Moved [{engine}]: {fileName} → {_destinationFolder}");
            }
            else
            {
                await Task.Run(() => File.Delete(filePath));
                FileDeleted?.Invoke(this, $"Deleted: {fileName}");
            }

            // Remove from UI
            group.Files.Remove(file);
            if (group.Files.Count <= 1)
            {
                Groups.Remove(group);
                SelectedGroup = Groups.FirstOrDefault();
            }
            else
            {
                SelectedFile = group.Files.FirstOrDefault(f => f != file);
            }

            _totalGroups     = Groups.Count;
            _totalDuplicates = Groups.Sum(g => g.Files.Count - 1);
            RaiseProperty(nameof(SummaryText));
            RaiseProperty(nameof(ScanSummaryLine));
            RaiseProperty(nameof(GroupCountLabel));
            RaiseProperty(nameof(HasGroups));
            RaiseProperty(nameof(HasNoGroups));
            ScanStatus = $"Removed: {fileName}. {ScanSummaryLine}";

            // Clear hash cache so next scan reflects the deletion
            DuplicateDetectorService.ClearHashCache();
        }
        catch (Exception ex)
        {
            var msg = $"Could not {(doMove ? "move" : "delete")} {fileName}: {ex.Message}";
            ErrorOccurred?.Invoke(this, msg);
            _dialogs.Show(msg, "Error",
                Services.DialogButtons.Ok, Services.DialogIcon.Error);
        }
    }

    // ── fpcalc.exe installer ─────────────────────────────────────────────────

    private async Task RunInstallFpcalcAsync()
    {
        IsInstallingFpcalc = true;
        DeepScanStatus     = "Checking for fpcalc.exe…";

        var progress = new Progress<(int pct, string status)>(p =>
        {
            DeepScanProgress = p.pct;
            DeepScanStatus   = p.status;
        });

        var (result, message) = await Services.FpcalcInstallerService.CheckAndInstallAsync(
            progress, CancellationToken.None);

        IsInstallingFpcalc = false;

        if (result == Services.FpcalcInstallerService.InstallResult.Installed
         || result == Services.FpcalcInstallerService.InstallResult.Updated
         || result == Services.FpcalcInstallerService.InstallResult.AlreadyCurrent)
        {
            ChromaprintAvailable = Services.NativeLibraryExtractor.IsFpcalcAvailable();
            FpcalcVersion = await Services.FpcalcInstallerService.GetInstalledVersionAsync()
                            ?? string.Empty;
        }

        DeepScanStatus = message;
    }

    // ── Deep Scan (audio fingerprinting) ─────────────────────────────────────

    private async Task RunDeepScanAsync()
    {
        if (IsDeepScanning) return;
        IsDeepScanning   = true;
        DeepScanProgress = 0;
        DeepScanStatus   = "Preparing deep scan…";

        _deepScanCts?.Cancel();
        _deepScanCts = new CancellationTokenSource();
        var ct = _deepScanCts.Token;

        int excludedCount = 0;
        // Collect all video files from source (and optional dest) folders.
        // Populate FileInfo + embedded metadata so the duplicate group display
        // shows real sizes and the side-by-side dialog can label files correctly.
        var allFilesRaw = new List<VideoFile>();
        void AddFolder(string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;
            var ext = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm" };
            foreach (var f in Directory.EnumerateFiles(folder,
                "*", _includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            {
                if (!ext.Contains(Path.GetExtension(f))) continue;
                if (IsExcludedByPattern(f)) { excludedCount++; continue; }
                try
                {
                    var info = new FileInfo(f);
                    if (info.Length == 0) continue; // skip empty files

                    var embedded = _metaSvc.ReadMetadataFast(f);
                    var (pt, py) = Services.FilenameParser.Parse(Path.GetFileNameWithoutExtension(f));

                    // Promote parsed title/year if embedded fields are empty
                    if (!string.IsNullOrWhiteSpace(pt) && string.IsNullOrWhiteSpace(embedded.Title))
                        embedded.Title = pt;
                    if (!string.IsNullOrWhiteSpace(py) && string.IsNullOrWhiteSpace(embedded.Year))
                        embedded.Year = py;

                    allFilesRaw.Add(new VideoFile
                    {
                        FilePath         = f,
                        FileSizeBytes    = info.Length,
                        EmbeddedMetadata = embedded,
                        ParsedTitle      = pt,
                        ParsedYear       = py,
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[DeepScan] Skipped '{f}': {ex.Message}");
                }
            }
        }
        AddFolder(SourceFolder);
        AddFolder(DestinationFolder);

        // Deduplicate by full path — handles cases where source==destination,
        // or one folder is a subfolder of the other.
        var allFiles = allFilesRaw
            .GroupBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Deduplicate — same physical file may live in both folders if one is
        // inside the other, or via junctions/symlinks; also handles re-scan races.
        allFiles = allFiles
            .GroupBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        if (allFiles.Count == 0)
        {
            DeepScanStatus = "No video files found. Set a source folder first.";
            IsDeepScanning = false;
            return;
        }

        var excludedNote = excludedCount > 0 ? $" ({excludedCount} excluded by pattern)" : string.Empty;
            DeepScanStatus = $"Deep scanning {allFiles.Count:N0} file(s){excludedNote} across {Math.Max(1, Environment.ProcessorCount - 1)} CPU core(s)…";

        try
        {
            var progress = new Progress<(int pct, string status)>(p =>
            {
                DeepScanProgress = p.pct;
                DeepScanStatus   = p.status;
            });

            // Use all available cores minus one to keep UI responsive
            int workers = Math.Max(1, Environment.ProcessorCount - 1);
            var newGroups = await Services.DuplicateDetectorService.DeepScanAsync(
                allFiles, _fpCache,
                similarityThreshold: 0.70,
                progress: progress,
                maxParallelism: workers,
                ct: ct);

            if (ct.IsCancellationRequested) return;

            int added = 0;
            foreach (var g in newGroups)
            {
                // Wrap in DuplicateGroupViewModel and add to Groups
                var gvm = new DuplicateGroupViewModel(g, SourceFolder ?? string.Empty,
                                                         DestinationFolder ?? string.Empty);
                // Check not already in list (exact match on confidence+reason)
                if (!Groups.Any(existing => existing.Reason == gvm.Reason))
                {
                    Groups.Add(gvm);
                    added++;
                }
            }

            DeepScanProgress = 100;
            DeepScanStatus   = added > 0
                ? $"✓ Deep scan complete — {added} audio duplicate group(s) found."
                : $"✓ Deep scan complete — no additional audio duplicates found.";

            RaiseProperty(nameof(HasGroups));
            RaiseProperty(nameof(HasNoGroups));
            RaiseProperty(nameof(SummaryText));
        }
        catch (OperationCanceledException)
        {
            DeepScanStatus = "Deep scan cancelled.";
        }
        catch (Exception ex)
        {
            DeepScanStatus = $"Deep scan error: {ex.Message}";
        }
        finally
        {
            IsDeepScanning = false;
        }
    }

    // ── Smart selection (#8) ─────────────────────────────────────────────────

    /// <summary>
    /// Selects files across ALL groups based on a rule.
    /// Updates IsSelected on the underlying DuplicateFileViewModel objects which
    /// are bound to checkboxes in the file list. The user can then click Delete
    /// or Move to act on everything at once.
    /// </summary>
    private void SmartSelect(SmartSelectMode mode)
    {
        int count = 0;
        foreach (var group in Groups)
        {
            if (group.Files.Count < 2) continue;

            // Determine which files in this group match the mode
            DuplicateFileViewModel? toSelect = mode switch
            {
                SmartSelectMode.Smaller    => group.Files.OrderBy(f => f.FileSizeBytes).First(),
                SmartSelectMode.Larger     => group.Files.OrderByDescending(f => f.FileSizeBytes).First(),
                SmartSelectMode.Untagged   => group.Files.FirstOrDefault(f => !f.HasMetadataTitle),
                SmartSelectMode.InUnsorted => group.Files.FirstOrDefault(f =>
                    f.FolderPath.Contains("\\Unsorted\\", StringComparison.OrdinalIgnoreCase)),
                _ => null
            };

            // Clear all first, then mark only the chosen one
            foreach (var f in group.Files) f.IsSelected = false;
            if (mode == SmartSelectMode.None) continue;
            if (toSelect != null)
            {
                toSelect.IsSelected = true;
                count++;
            }
        }

        var modeLabel = mode switch
        {
            SmartSelectMode.Smaller    => "smaller files",
            SmartSelectMode.Larger     => "larger files",
            SmartSelectMode.Untagged   => "untagged files",
            SmartSelectMode.InUnsorted => "files in \\Unsorted\\",
            _                          => "all selections"
        };
        ScanStatus = mode == SmartSelectMode.None
            ? "All selections cleared."
            : $"Selected {count} {modeLabel} across {Groups.Count} group(s).";

        RaiseProperty(nameof(SelectedFileCount));
        RaiseProperty(nameof(SelectedFilesSize));
    }

    // ── Auto-resolve (#4) ────────────────────────────────────────────────────

    /// <summary>
    /// Picks files for deletion based on an auto-resolve rule.
    /// Same UI surface as SmartSelect — sets IsSelected; user reviews then deletes.
    /// Skips groups where the rule can't decide (e.g. all files equally tagged).
    /// </summary>
    private void AutoResolve(AutoResolveRule rule)
    {
        int decided = 0, skipped = 0;
        foreach (var group in Groups)
        {
            if (group.Files.Count < 2) continue;
            foreach (var f in group.Files) f.IsSelected = false;

            DuplicateFileViewModel? keeper = rule switch
            {
                AutoResolveRule.KeepLargest => group.Files.OrderByDescending(f => f.FileSizeBytes).First(),
                AutoResolveRule.KeepNewest  => group.Files.OrderByDescending(f => f.CreatedRaw).First(),
                AutoResolveRule.KeepTagged  => group.Files.FirstOrDefault(f => f.HasMetadataTitle)
                                            ?? group.Files.First(),
                _ => null
            };

            if (keeper == null) { skipped++; continue; }

            // Verify the rule actually distinguishes — if everything's equal, skip
            bool meaningful = rule switch
            {
                AutoResolveRule.KeepLargest => group.Files.Select(f => f.FileSizeBytes).Distinct().Count() > 1,
                AutoResolveRule.KeepNewest  => group.Files.Select(f => f.CreatedRaw).Distinct().Count() > 1,
                AutoResolveRule.KeepTagged  => group.Files.Any(f => f.HasMetadataTitle)
                                            && group.Files.Any(f => !f.HasMetadataTitle),
                _ => false
            };
            if (!meaningful) { skipped++; continue; }

            foreach (var f in group.Files)
                if (f != keeper) f.IsSelected = true;
            decided++;
        }

        var ruleLabel = rule switch
        {
            AutoResolveRule.KeepLargest => "Keep largest",
            AutoResolveRule.KeepNewest  => "Keep newest",
            AutoResolveRule.KeepTagged  => "Keep tagged",
            _ => "Auto-resolve"
        };
        ScanStatus = $"✓ {ruleLabel}: {decided} group(s) auto-resolved, " +
                     $"{skipped} skipped (no clear winner). " +
                     "Review selections in the file list, then click Delete or Move.";

        RaiseProperty(nameof(SelectedFileCount));
        RaiseProperty(nameof(SelectedFilesSize));
    }

    // ── Selection summary properties ─────────────────────────────────────────

    public int SelectedFileCount => Groups
        .SelectMany(g => g.Files)
        .Count(f => f.IsSelected);

    public string SelectedFilesSize
    {
        get
        {
            long bytes = Groups.SelectMany(g => g.Files)
                               .Where(f => f.IsSelected)
                               .Sum(f => f.FileSizeBytes);
            return bytes switch
            {
                >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
                >= 1_048_576     => $"{bytes / 1_048_576.0:F1} MB",
                _                => $"{bytes / 1024.0:F1} KB"
            };
        }
    }

    // ── Reference scan (#1) ──────────────────────────────────────────────────

    /// <summary>
    /// Audio-fingerprints a single reference file, then sweeps the library for matches.
    /// O(n) — completes in seconds rather than the O(n²) full Deep Scan.
    /// </summary>
    private async Task RunReferenceScanAsync()
    {
        var referencePath = _dialogs.PickOpenFile(
            "Pick a reference file — find duplicates of this in the library",
            "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.m4v;*.webm|All files|*.*");
        if (referencePath == null) return;

        IsDeepScanning = true;
        DeepScanProgress = 0;
        DeepScanStatus = $"Computing reference fingerprint…";

        _deepScanCts = new CancellationTokenSource();
        var ct = _deepScanCts.Token;

        try
        {
            // Compute reference fingerprint (or take from cache)
            var refFp = _fpCache.TryGet(referencePath)
                ?? await Services.ChromaprintService.ComputeFingerprintAsync(referencePath, ct: ct);
            if (string.IsNullOrEmpty(refFp))
            {
                DeepScanStatus = "Couldn\u0027t fingerprint reference file (no audio?). " +
                                 "Try a different file.";
                return;
            }
            _fpCache.Store(referencePath, refFp);

            DeepScanStatus = "Scanning library against reference…";

            // Build list of candidates from Source folder (or library)
            var candidates = new List<VideoFile>();
            if (!string.IsNullOrWhiteSpace(SourceFolder) && Directory.Exists(SourceFolder))
            {
                var ext = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".m4v", ".webm" };
                foreach (var f in Directory.EnumerateFiles(SourceFolder, "*",
                    _includeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                {
                    if (string.Equals(f, referencePath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!ext.Contains(Path.GetExtension(f))) continue;
                    try { candidates.Add(BuildVideoFileFromPath(f)); }
                    catch { /* skip unreadable */ }
                }
            }

            if (candidates.Count == 0)
            {
                DeepScanStatus = "No candidates found. Set a Source folder first.";
                return;
            }

            // Fingerprint each candidate and compare against reference
            int workers = Math.Max(1, Environment.ProcessorCount - 1);
            var matches = new System.Collections.Concurrent.ConcurrentBag<(VideoFile, double)>();
            int done = 0;

            await Parallel.ForEachAsync(candidates,
                new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct },
                async (vf, innerCt) =>
                {
                    var fp = _fpCache.TryGet(vf.FilePath)
                        ?? await Services.ChromaprintService.ComputeFingerprintAsync(
                            vf.FilePath, ct: innerCt) ?? string.Empty;
                    _fpCache.Store(vf.FilePath, string.IsNullOrEmpty(fp) ? null : fp);

                    if (!string.IsNullOrEmpty(fp))
                    {
                        var sim = Services.ChromaprintService.ComputeSimilarity(refFp, fp);
                        if (sim >= 0.70)
                            matches.Add((vf, sim));
                    }

                    var d   = Interlocked.Increment(ref done);
                    var pct = (int)(d * 100.0 / candidates.Count);
                    DeepScanProgress = pct;
                    DeepScanStatus   = $"Reference scan {d:N0}/{candidates.Count:N0}  ·  " +
                                       $"{matches.Count} match(es)";
                });

            _fpCache.SaveIfDirty();

            // Build the reference file as a VideoFile (so it can appear in the group)
            var refVf = BuildVideoFileFromPath(referencePath);

            foreach (var (vf, sim) in matches.OrderByDescending(m => m.Item2))
            {
                var pair = new List<VideoFile> { refVf, vf }.OrderBy(f => f.FileSizeBytes).ToList();
                var g    = new Services.DuplicateGroup(pair,
                    Services.DuplicateConfidence.AudioMatch,
                    $"Reference match ({sim:P0}) — {Path.GetFileName(vf.FilePath)}");
                Groups.Add(new DuplicateGroupViewModel(g, SourceFolder ?? string.Empty,
                                                          DestinationFolder ?? string.Empty));
            }

            DeepScanStatus = matches.Count > 0
                ? $"✓ Reference scan complete  ·  {matches.Count} match(es) for {Path.GetFileName(referencePath)}"
                : $"✓ Reference scan complete  ·  No matches for {Path.GetFileName(referencePath)}";

            RaiseProperty(nameof(HasGroups));
            RaiseProperty(nameof(HasNoGroups));
            RaiseProperty(nameof(SummaryText));
        }
        catch (OperationCanceledException)
        {
            DeepScanStatus = "Reference scan cancelled.";
        }
        catch (Exception ex)
        {
            DeepScanStatus = $"Reference scan error: {ex.Message}";
        }
        finally
        {
            IsDeepScanning = false;
        }
    }

    // ── Fingerprint cache export/import (#2) ─────────────────────────────────

    private void ExportFingerprintCache()
    {
        var exportPath = _dialogs.PickSaveFile(
            "Export fingerprint cache", "JSON|*.json",
            $"vme_fingerprints_{DateTime.Now:yyyyMMdd}.json");
        if (exportPath == null) return;

        try
        {
            // Cache is already saved as JSON — just copy the file
            var src = Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
                "fingerprints.json");
            if (!File.Exists(src))
            {
                ScanStatus = "No fingerprints to export yet. Run Deep Scan first.";
                return;
            }
            File.Copy(src, exportPath, overwrite: true);
            ScanStatus = $"✓ Fingerprint cache exported to {Path.GetFileName(exportPath)}";
        }
        catch (Exception ex)
        {
            ScanStatus = $"Export failed: {ex.Message}";
        }
    }

    private void ImportFingerprintCache()
    {
        var importPath = _dialogs.PickOpenFile("Import fingerprint cache", "JSON|*.json");
        if (importPath == null) return;

        try
        {
            var dest = Path.Combine(
                Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory,
                "fingerprints.json");

            // Ask whether to merge or replace when a cache already exists
            bool merge = false;
            if (File.Exists(dest))
            {
                var res = _dialogs.Show(
                    "An existing fingerprint cache was found.\n\n" +
                    "  • Merge  — add the imported entries to your existing cache\n" +
                    "  • Replace  — discard your existing cache and use the imported one\n\n" +
                    "Choose Merge to keep both.",
                    "Import fingerprint cache",
                    Services.DialogButtons.YesNoCancel,
                    Services.DialogIcon.Question);
                if (res == Services.DialogResult.Cancel) return;
                merge = res == Services.DialogResult.Yes; // Yes=Merge, No=Replace
            }

            if (merge)
            {
                // Merge: load the import file into the cache without touching the existing file
                _fpCache.MergeFrom(importPath);
                _fpCache.SaveIfDirty();
            }
            else
            {
                File.Copy(importPath, dest, overwrite: true);
                _fpCache.Reload();   // live reload — no restart needed
            }

            ScanStatus = $"✓ Fingerprint cache imported from {Path.GetFileName(importPath)}.";
        }
        catch (Exception ex)
        {
            ScanStatus = $"Import failed: {ex.Message}";
        }
    }

    // ── Side-by-side comparison dialog ─────────────────────────────────────

    /// <summary>
    /// Reads FileInfo + embedded metadata for a single path and returns a fully
    /// populated VideoFile. Used by the compare dialog so size/title/year are
    /// available for display labels.
    /// </summary>
    /// <summary>
    /// Returns true if the file path matches any user-defined exclude pattern.
    /// Patterns use simple wildcard semantics: * matches any chars, case-insensitive.
    /// One pattern per line in Settings.DuplicateExcludedPatterns.
    /// </summary>
    private bool IsExcludedByPattern(string filePath)
    {
        var patterns = App.ConfigService.Settings.DuplicateExcludedPatterns;
        if (string.IsNullOrWhiteSpace(patterns)) return false;

        foreach (var raw in patterns.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var pattern = raw.Trim();
            if (string.IsNullOrEmpty(pattern)) continue;

            // Convert simple wildcard pattern to regex
            var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*").Replace("\\?", ".") + "$";
            try
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(filePath, regex,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return true;
            }
            catch { /* malformed pattern — skip */ }
        }
        return false;
    }

    private VideoFile BuildVideoFileFromPath(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return new VideoFile { FilePath = filePath };

            var embedded = _metaSvc.ReadMetadataFast(filePath);
            var (pt, py) = Services.FilenameParser.Parse(
                Path.GetFileNameWithoutExtension(filePath));

            if (string.IsNullOrWhiteSpace(embedded.Title) && !string.IsNullOrWhiteSpace(pt))
                embedded.Title = pt;
            if (string.IsNullOrWhiteSpace(embedded.Year)  && !string.IsNullOrWhiteSpace(py))
                embedded.Year  = py;

            return new VideoFile
            {
                FilePath         = filePath,
                FileSizeBytes    = info.Length,
                EmbeddedMetadata = embedded,
                ParsedTitle      = pt,
                ParsedYear       = py,
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Compare] Failed to read '{filePath}': {ex.Message}");
            return new VideoFile { FilePath = filePath };
        }
    }

    /// <summary>
    /// Supplied by the View: shows the side-by-side compare dialog for two files and
    /// returns the path the user chose to delete (or null if cancelled/none). Keeps the
    /// custom WPF compare window out of the ViewModel. If unset (headless), comparison
    /// is a no-op.
    /// </summary>
    public Func<VideoFile, VideoFile, string?>? CompareDialogRequested { get; set; }

    private void OpenCompareDialog(DuplicateGroupViewModel? groupVm)
    {
        if (groupVm == null || groupVm.Files.Count < 2) return;
        if (CompareDialogRequested == null) return;

        // Build VideoFile objects with full FileInfo + metadata so the dialog
        // can show real sizes and label which side is larger.
        var leftVm  = groupVm.Files[0];
        var rightVm = groupVm.Files[1];
        var left    = BuildVideoFileFromPath(leftVm.FilePath);
        var right   = BuildVideoFileFromPath(rightVm.FilePath);

        var deletedPath = CompareDialogRequested(left, right);

        if (deletedPath != null)
        {
            try
            {
                // Move to Recycle Bin (safe delete)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    deletedPath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);

                FileDeleted?.Invoke(this, deletedPath);

                // Remove the deleted file from this group
                var updatedFiles = groupVm.Files
                    .Where(f => !string.Equals(f.FilePath, deletedPath,
                        StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (updatedFiles.Count < 2)
                {
                    // Group resolved — remove it entirely
                    var idx = Groups.IndexOf(groupVm);
                    if (idx >= 0) Groups.RemoveAt(idx);
                    if (SelectedGroup == groupVm)
                    {
                        SelectedGroup = Groups.Count > 0 ? Groups[0] : null;
                        SelectedFile  = null;
                    }
                }
                RaiseProperty(nameof(HasGroups));
                RaiseProperty(nameof(HasNoGroups));
                RaiseProperty(nameof(SummaryText));
                RaiseProperty(nameof(ScanSummaryLine));
            }
            catch (Exception ex)
            {
                ErrorOccurred?.Invoke(this, $"Delete failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Removes duplicate groups that contain the same file set caught by multiple
    /// detection stages. When two groups share identical members, the one with
    /// higher confidence wins; if equal confidence, the one with more files wins.
    /// </summary>
    private static IReadOnlyList<DuplicateGroup> DeduplicateGroups(
        IEnumerable<DuplicateGroup> groups)
    {
        var result = new List<DuplicateGroup>();
        var seen   = new Dictionary<string, int>();

        foreach (var g in groups)
        {
            var key = string.Join("|",
                g.Files.Select(f => f.FilePath.ToLowerInvariant()).OrderBy(x => x));

            if (seen.TryGetValue(key, out int idx))
            {
                var existing = result[idx];
                if ((int)g.Confidence > (int)existing.Confidence
                    || (g.Confidence == existing.Confidence
                        && g.Files.Count > existing.Files.Count))
                    result[idx] = g;
            }
            else
            {
                seen[key] = result.Count;
                result.Add(g);
            }
        }
        return result;
    }
}

// ── Group view model ──────────────────────────────────────────────────────────

public class DuplicateGroupViewModel : ViewModelBase
{
    public string ConfidenceLabel { get; }
    public string Reason          { get; }
    public string GroupSizeLabel  { get; }
    public ObservableCollection<DuplicateFileViewModel> Files { get; }

    public DuplicateGroupViewModel(
        DuplicateGroup group, string sourceFolder, string destFolder)
    {
        ConfidenceLabel = group.ConfidenceLabel;
        Reason          = group.Reason;
        Confidence      = group.Confidence;
        GroupSizeLabel  = $"{group.Files.Count} files  ·  {group.ConfidenceLabel}";
        Files = new ObservableCollection<DuplicateFileViewModel>(
            group.Files.Select(f => new DuplicateFileViewModel(f, sourceFolder)));

        // Bubble individual file IsSelected changes upward so the parent
        // DuplicatesViewModel can refresh its summary counts in real time.
        foreach (var f in Files)
            f.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(DuplicateFileViewModel.IsSelected))
                    FileSelectionChanged?.Invoke(this, EventArgs.Empty);
            };
    }

    public DuplicateConfidence Confidence { get; }
    public event EventHandler? FileSelectionChanged;
}

// ── File view model ───────────────────────────────────────────────────────────

public class DuplicateFileViewModel : ViewModelBase
{
    public string FilePath    { get; }
    public string FileName    { get; }
    public string FolderPath  { get; }
    public string FileSizeDisplay { get; }
    public long   FileSizeBytes { get; }
    public bool   HasMetadataTitle { get; }

    // Live selection state — drives smart-select & auto-resolve workflows
    private bool _isSelected;
    public  bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; RaiseProperty(); } }
    }

    public string Format      { get; }
    public string Title       { get; }
    public string Year        { get; }
    public string Genre       { get; }
    public string Director    { get; }
    public string ImdbId      { get; }
    public string TmdbId      { get; }
    public string RatingDisplay { get; }
    public string CreatedDisplay  { get; }
    public string ModifiedDisplay { get; }
    public string Resolution  { get; }
    public string VideoCodec  { get; }
    public string AudioCodec  { get; }
    public string Duration    { get; }
    public bool     IsInSourceFolder { get; }
    public DateTime CreatedRaw       { get; private set; }

    // Folder badge shown in file list row
    public string FolderBadgeLabel => IsInSourceFolder ? "SRC" : "DST";
    public string FolderBadgeColor => IsInSourceFolder ? "#1565C0" : "#558B2F";

    // For comparison highlighting — set after both files in a pair are created
    public bool SizeMatch     { get; set; }
    public bool TitleMatch    { get; set; }
    public bool YearMatch     { get; set; }
    public bool ImdbMatch     { get; set; }

    public DuplicateFileViewModel(VideoFile vf, string sourceFolder)
    {
        FilePath   = vf.FilePath;
        FileName   = vf.FileName;
        FolderPath = Path.GetDirectoryName(vf.FilePath) ?? string.Empty;
        IsInSourceFolder = !string.IsNullOrWhiteSpace(sourceFolder)
            && vf.FilePath.StartsWith(sourceFolder, StringComparison.OrdinalIgnoreCase);

        FileSizeBytes = vf.FileSizeBytes;
        var bytes = vf.FileSizeBytes;
        FileSizeDisplay = bytes >= 1_073_741_824
            ? $"{bytes / 1_073_741_824.0:F2} GB"
            : $"{bytes / 1_048_576.0:F1} MB";

        var m  = vf.EmbeddedMetadata;
        HasMetadataTitle = !string.IsNullOrWhiteSpace(m.Title)
                       || !string.IsNullOrWhiteSpace(m.ShowTitle);
        Title  = m.Title;
        Year   = m.Year;
        Genre  = m.Genre;
        Director = m.Director;
        ImdbId = m.ImdbId;
        TmdbId = m.TmdbId;
        RatingDisplay = m.Rating > 0 ? $"{m.Rating:F1}/10" : string.Empty;
        Format = vf.Extension.TrimStart('.').ToUpperInvariant();

        try
        {
            var info = new FileInfo(vf.FilePath);
            CreatedRaw      = info.CreationTime;
            CreatedDisplay  = info.CreationTime.ToString("yyyy-MM-dd  HH:mm:ss");
            ModifiedDisplay = info.LastWriteTime.ToString("yyyy-MM-dd  HH:mm:ss");
        }
        catch { CreatedRaw = DateTime.MinValue; CreatedDisplay = ModifiedDisplay = string.Empty; }

        // Technical info via MediaInfoService
        try
        {
            var tech = Services.MediaInfoService.Read(vf.FilePath);
            Resolution = tech?.ResolutionDisplay ?? string.Empty;
            VideoCodec = tech?.VideoCodec        ?? string.Empty;
            AudioCodec = tech?.AudioCodec        ?? string.Empty;
            Duration   = tech?.DurationDisplay   ?? string.Empty;
        }
        catch { Resolution = VideoCodec = AudioCodec = Duration = string.Empty; }
    }
}
