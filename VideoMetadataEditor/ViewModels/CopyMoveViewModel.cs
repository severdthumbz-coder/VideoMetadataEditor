using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;

namespace VideoMetadataEditor.ViewModels;

/// <summary>
/// Owns all copy/move state: destination, conflict mode, engine selection,
/// Smart Organise, progress, disk space, and post-transfer cleanup.
/// </summary>
public class CopyMoveViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly Func<ObservableCollection<VideoFile>> _getFiles;

    // ── Events ────────────────────────────────────────────────────────────────
    public event EventHandler<TransferEventArgs>? CopyCompleted;
    public event EventHandler<TransferEventArgs>? MoveCompleted;

    // ── State ─────────────────────────────────────────────────────────────────
    private string _destination = string.Empty;
    public string Destination
    {
        get => _destination;
        set { Set(ref _destination, value); RaiseProperty(nameof(SmartMoviesPreviewPath)); RaiseProperty(nameof(SmartTvPreviewPath)); }
    }

    private string _conflictMode = "Skip";
    public string ConflictMode
    {
        get => _conflictMode;
        set { Set(ref _conflictMode, value); RaiseProperty(nameof(ConflictModeDescription)); }
    }

    public string ConflictModeDescription => _conflictMode switch
    {
        "Overwrite"        => "Existing files at destination will be overwritten.",
        "OverwriteIfNewer" => "Overwrite only if source file is newer than destination.",
        "Rename"           => "Conflicting files will be renamed with a numeric suffix.",
        _                  => "Existing files at destination will be skipped."
    };

    private bool _isRunning;
    public bool IsRunning { get => _isRunning; private set { Set(ref _isRunning, value); CommandManager.InvalidateRequerySuggested(); } }

    private int _progressValue;
    public int ProgressValue { get => _progressValue; set => Set(ref _progressValue, value); }

    private string _diskSpaceStatus = string.Empty;
    public string DiskSpaceStatus { get => _diskSpaceStatus; set => Set(ref _diskSpaceStatus, value); }

    private string _spaceStatusMsg = string.Empty;
    public string SpaceStatusMsg { get => _spaceStatusMsg; set => Set(ref _spaceStatusMsg, value); }

    private bool _spaceIsSufficient = true;
    public bool SpaceIsSufficient { get => _spaceIsSufficient; set => Set(ref _spaceIsSufficient, value); }

    private string _detectedEngineText = string.Empty;
    public string DetectedEngineText { get => _detectedEngineText; set => Set(ref _detectedEngineText, value); }

    public bool AutoEngineAllowed => true; // auto always available (falls back to BuiltIn)
    public bool FastCopyAllowed   => FileCopyService.FastCopyAvailable;

    // Settings pass-throughs (no state duplication — reads directly from AppSettings)
    public bool SmartOrganiseEnabled
    {
        get => _settings.SmartOrganiseEnabled;
        set { _settings.SmartOrganiseEnabled = value; RaiseProperty(); RaiseProperty(nameof(SmartMoviesPreviewPath)); RaiseProperty(nameof(SmartTvPreviewPath)); _ = App.ConfigService.SaveAsync(); }
    }

    public bool CreateSeasonSubfolders
    {
        get => _settings.CreateSeasonSubfolders;
        set { _settings.CreateSeasonSubfolders = value; RaiseProperty(); RaiseProperty(nameof(SmartTvPreviewPath)); _ = App.ConfigService.SaveAsync(); }
    }

    public string SavedCopyDestination => _settings.LastCopyDestination ?? string.Empty;
    public string SavedConflictMode    => _settings.LastConflictMode    ?? "Skip";

    public string SmartMoviesPreviewPath
    {
        get
        {
            var dest = (Destination ?? "").TrimEnd(Path.DirectorySeparatorChar);
            var folder = string.IsNullOrWhiteSpace(_settings.MoviesFolderName) ? "Movies" : _settings.MoviesFolderName;
            return Path.Combine(dest, folder) + Path.DirectorySeparatorChar;
        }
    }

    public string SmartTvPreviewPath
    {
        get
        {
            var dest = (Destination ?? "").TrimEnd(Path.DirectorySeparatorChar);
            var folder = string.IsNullOrWhiteSpace(_settings.TvShowsFolderName) ? "TV Shows" : _settings.TvShowsFolderName;
            return Path.Combine(dest, folder) + @"\{Show}\Season ##\";
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    public ICommand CopyCommand              { get; }
    public ICommand MoveCommand              { get; }
    public ICommand BrowseDestCommand        { get; }
    public ICommand AnalyzeSpaceCommand      { get; }
    public ICommand SaveSettingsCommand      { get; }
    public ICommand ClearSavedDestCommand    { get; }
    public CancellationTokenSource? ActiveCts { get; private set; }

    // Delegates set by MainViewModel for cross-VM operations
    public Func<CopyMode, Task>? ExecuteTransferAsync { get; set; }
    public Action?               BrowseDestination    { get; set; }

    public CopyMoveViewModel(AppSettings settings, Func<ObservableCollection<VideoFile>> getFiles)
    {
        _settings = settings;
        _getFiles = getFiles;

        // Restore saved destination
        if (!string.IsNullOrWhiteSpace(settings.LastCopyDestination))
            _destination = settings.LastCopyDestination;
        _conflictMode = settings.LastConflictMode ?? "Skip";

        CopyCommand           = new AsyncRelayCommand(() => ExecuteTransferAsync?.Invoke(CopyMode.Copy) ?? Task.CompletedTask,
            _ => !IsRunning && !string.IsNullOrWhiteSpace(Destination) && _getFiles().Any(f => f.IsSelected && !f.IsSeparator));
        MoveCommand           = new AsyncRelayCommand(() => ExecuteTransferAsync?.Invoke(CopyMode.Move) ?? Task.CompletedTask,
            _ => !IsRunning && !string.IsNullOrWhiteSpace(Destination) && _getFiles().Any(f => f.IsSelected && !f.IsSeparator));
        BrowseDestCommand     = new RelayCommand(_ => BrowseDestination?.Invoke());
        AnalyzeSpaceCommand   = new AsyncRelayCommand(AnalyzeSpaceInternalAsync);
        SaveSettingsCommand   = new RelayCommand(_ => SaveSettings());
        ClearSavedDestCommand = new RelayCommand(_ => ClearSavedDest());
    }

    public void SaveSettings()
    {
        _settings.LastCopyDestination = Destination;
        _settings.LastConflictMode    = ConflictMode;
        _ = App.ConfigService.SaveAsync();
        RaiseProperty(nameof(SavedCopyDestination));
        RaiseProperty(nameof(SavedConflictMode));
    }

    private void ClearSavedDest()
    {
        _settings.LastCopyDestination = string.Empty;
        _ = App.ConfigService.SaveAsync();
        RaiseProperty(nameof(SavedCopyDestination));
    }

    private async Task AnalyzeSpaceInternalAsync()
    {
        var sources = _getFiles()
            .Where(f => f.IsSelected && !f.IsSeparator)
            .Select(f => f.FilePath).ToList();
        if (sources.Count == 0 || string.IsNullOrWhiteSpace(Destination)) return;

        var analysis = FileCopyService.AnalyseDiskSpace(sources, Destination);
        DiskSpaceStatus  = analysis.Message;
        SpaceIsSufficient = analysis.HasSufficientSpace;
        SpaceStatusMsg   = analysis.Message;
    }

    public void SetRunning(bool running) => IsRunning = running;

    public void RaiseEngineProperties()
    {
        RaiseProperty(nameof(AutoEngineAllowed));
        RaiseProperty(nameof(FastCopyAllowed));
        RaiseProperty(nameof(DetectedEngineText));
    }

    public void FireCopyCompleted(TransferEventArgs e) => CopyCompleted?.Invoke(this, e);
    public void FireMoveCompleted(TransferEventArgs e) => MoveCompleted?.Invoke(this, e);
}
