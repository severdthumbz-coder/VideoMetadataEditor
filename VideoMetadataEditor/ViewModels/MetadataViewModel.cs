using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;

namespace VideoMetadataEditor.ViewModels;

/// <summary>
/// Owns all metadata-editing state: the editing form fields, artwork,
/// rename preview, write status, and all embed/rename commands.
/// </summary>
public class MetadataViewModel : ViewModelBase
{
    private readonly AppSettings    _settings;
    private readonly MetadataService _metaService;
    private readonly FileRenameService _renameService;


    // ── Events ────────────────────────────────────────────────────────────────
    public event EventHandler<RenameEventArgs>? RenameSucceeded;
    public event EventHandler<RenameEventArgs>? RenameFailed;
    public event EventHandler<EmbedEventArgs>?  EmbedSucceeded;

    // ── Selected file ─────────────────────────────────────────────────────────
    private VideoFile? _selectedFile;
    public VideoFile? SelectedFile
    {
        get => _selectedFile;
        set
        {
            Set(ref _selectedFile, value);
            RaiseFileProperties();
        }
    }

    private void RaiseFileProperties()
    {
        RaiseProperty(nameof(OriginalFileName));
        RaiseProperty(nameof(ParsedTitle));
        RaiseProperty(nameof(ParsedYear));
        RaiseProperty(nameof(ParsedFileInfo));
        RaiseProperty(nameof(FormatSupportsFullTags));
        RaiseProperty(nameof(FormatSupportsArtwork));
        RaiseProperty(nameof(FormatWarning));
        RaiseProperty(nameof(SelectedFileIsReadOnly));
    }

    // ── Editing state (backed by EditingMetadata) ─────────────────────────────
    private MovieMetadata _editingMetadata = new();
    public MovieMetadata EditingMetadata
    {
        get => _editingMetadata;
        set { Set(ref _editingMetadata, value); UpdateRenamePreview(); }
    }

    private bool _isEpisodeMode;
    public bool IsEpisodeMode
    {
        get => _isEpisodeMode;
        set
        {
            Set(ref _isEpisodeMode, value);
            RaiseProperty(nameof(IsMovieMode));
            RaiseProperty(nameof(ActiveRenamePattern));
            UpdateRenamePreview();
        }
    }
    public bool IsMovieMode => !_isEpisodeMode;

    // ── Write state ───────────────────────────────────────────────────────────
    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { Set(ref _isBusy, value); CommandRequery.Invalidate(); } }

    private WriteStatus _writeStatus = WriteStatus.Untouched;
    public WriteStatus WriteStatus { get => _writeStatus; set => Set(ref _writeStatus, value); }

    // ── Rename preview ────────────────────────────────────────────────────────
    private string _renamePreview = string.Empty;
    public string RenamePreview { get => _renamePreview; set => Set(ref _renamePreview, value); }

    private readonly IUiDispatcher _ui;
    private System.Threading.CancellationTokenSource? _renamePreviewCts;

    // ── Smart rename pattern ──────────────────────────────────────────────────
    public string MovieRenamePattern
    {
        get => _settings.RenamePattern;
        set { _settings.RenamePattern = value; RaiseProperty(); UpdateRenamePreview(); _ = App.ConfigService.SaveAsync(); }
    }

    public string TvRenamePattern
    {
        get => _settings.TvRenamePattern;
        set { _settings.TvRenamePattern = value; RaiseProperty(); UpdateRenamePreview(); _ = App.ConfigService.SaveAsync(); }
    }

    public string ActiveRenamePattern => IsEpisodeMode ? _settings.TvRenamePattern : _settings.RenamePattern;

    public ObservableCollection<string> MovieRenamePresets => new(new[]
    {
        "{Title} ({Year})", "{Title} ({Year}) [{MPA}]", "{Title} ({Year}) [{Resolution}]",
        "{Title} ({Year}) [{Resolution}] [{MPA}]", "{Title} - {Director} ({Year})", "{Title} ({Year}) {Rating}",
    });

    public ObservableCollection<string> TvRenamePresets => new(new[]
    {
        "{ShowTitle} - S{Season}E{Episode} - {EpisodeTitle}",
        "{ShowTitle} S{Season}E{Episode}",
        "{ShowTitle} - S{Season}E{Episode}",
        "{ShowTitle} - {Season}x{Episode} - {EpisodeTitle}",
        "{ShowTitle} Season {Season} Episode {Episode} - {EpisodeTitle}",
    });

    // ── File format properties ────────────────────────────────────────────────
    public bool FormatSupportsFullTags
    {
        get
        {
            if (_selectedFile == null) return false;
            var fmt = MetadataService.DetectFormat(_selectedFile.FilePath);
            var (_, full, _) = MetadataService.GetFormatCapabilities(fmt);
            return full;
        }
    }
    public bool FormatSupportsArtwork
    {
        get
        {
            if (_selectedFile == null) return false;
            var fmt = MetadataService.DetectFormat(_selectedFile.FilePath);
            var (art, _, _) = MetadataService.GetFormatCapabilities(fmt);
            return art;
        }
    }
    public string FormatWarning
    {
        get
        {
            if (_selectedFile == null) return string.Empty;
            var fmt = MetadataService.DetectFormat(_selectedFile.FilePath);
            var (_, _, warn) = MetadataService.GetFormatCapabilities(fmt);
            return warn ?? string.Empty;
        }
    }

    public string OriginalFileName => _selectedFile?.FileName ?? string.Empty;
    public string ParsedTitle      => _selectedFile != null ? FilenameParser.Parse(Path.GetFileNameWithoutExtension(_selectedFile.FileName)).title : string.Empty;
    public string ParsedYear       => _selectedFile != null ? FilenameParser.Parse(Path.GetFileNameWithoutExtension(_selectedFile.FileName)).year  : string.Empty;
    public string ParsedFileInfo   => _selectedFile != null ? $"{_selectedFile.Extension.TrimStart('.').ToUpperInvariant()}  ·  {_selectedFile.FileSizeDisplay}" : string.Empty;

    public bool SelectedFileIsReadOnly => _selectedFile?.IsReadOnly ?? false;

    // ── Commands ──────────────────────────────────────────────────────────────
    public ICommand ApplyCommand          { get; }
    public ICommand UndoEmbedCommand      { get; }
    public ICommand RenameCommand         { get; }
    public ICommand BatchRenameCommand    { get; }
    public ICommand ExportNfoCommand      { get; }
    public ICommand BulkArtworkCommand    { get; }
    public ICommand CopyArtworkCommand    { get; }
    public ICommand PickArtworkCommand    { get; }
    public ICommand JumpToFailureCommand  { get; }
    public ICommand ToggleLockCommand     { get; }

    // Delegates set by MainViewModel for cross-VM operations
    public Func<Task>?            ApplyToFileAsync          { get; set; }
    public Func<Task>?            UndoLastEmbedAsync        { get; set; }
    public Func<Task>?            RenameCurrentFileAsync    { get; set; }
    public Func<Task>?            BatchRenameOnlyAsync      { get; set; }
    public Func<Task>?            ExportNfoAsync            { get; set; }
    public Func<Task>?            BulkDownloadArtworkAsync  { get; set; }
    public Action?                CopyArtworkToClipboard    { get; set; }
    public Action?                PickArtwork               { get; set; }
    public Action?                JumpToNextFailure         { get; set; }
    public Func<Task>?            ToggleLockAsync           { get; set; }

    public MetadataViewModel(AppSettings settings, MetadataService metaService,
        FileRenameService renameService, IUiDispatcher? ui = null)
    {
        _settings         = settings;
        _metaService      = metaService;
        _renameService    = renameService;
        _ui               = ui ?? NullUiDispatcher.Instance;


        ApplyCommand       = new AsyncRelayCommand(() => ApplyToFileAsync?.Invoke() ?? Task.CompletedTask,
            _ => _selectedFile != null && !IsBusy);
        UndoEmbedCommand   = new AsyncRelayCommand(() => UndoLastEmbedAsync?.Invoke() ?? Task.CompletedTask,
            _ => _selectedFile != null && !IsBusy);
        RenameCommand      = new AsyncRelayCommand(() => RenameCurrentFileAsync?.Invoke() ?? Task.CompletedTask,
            _ => _selectedFile != null && !IsBusy);
        BatchRenameCommand = new AsyncRelayCommand(() => BatchRenameOnlyAsync?.Invoke() ?? Task.CompletedTask,
            _ => !IsBusy);
        ExportNfoCommand   = new AsyncRelayCommand(() => ExportNfoAsync?.Invoke() ?? Task.CompletedTask,
            _ => !IsBusy);
        BulkArtworkCommand = new AsyncRelayCommand(() => BulkDownloadArtworkAsync?.Invoke() ?? Task.CompletedTask,
            _ => !IsBusy);
        CopyArtworkCommand = new RelayCommand(_ => CopyArtworkToClipboard?.Invoke());
        PickArtworkCommand = new RelayCommand(_ => PickArtwork?.Invoke());
        JumpToFailureCommand = new RelayCommand(_ => JumpToNextFailure?.Invoke());
        ToggleLockCommand  = new AsyncRelayCommand(() => ToggleLockAsync?.Invoke() ?? Task.CompletedTask,
            _ => _selectedFile != null);
    }

    public void UpdateRenamePreview()
    {
        // Debounce: cancel any pending preview and schedule a fresh one 150ms out.
        // Uses Task.Delay + cancellation (platform-agnostic) instead of DispatcherTimer;
        // the result is marshalled back to the UI thread via IUiDispatcher.
        var previous = _renamePreviewCts;
        previous?.Cancel();
        previous?.Dispose();
        _renamePreviewCts = new System.Threading.CancellationTokenSource();
        var ct = _renamePreviewCts.Token;

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await System.Threading.Tasks.Task.Delay(150, ct); }
            catch (OperationCanceledException) { return; }
            if (ct.IsCancellationRequested) return;

            _ui.Post(() =>
            {
                if (ct.IsCancellationRequested) return;
                if (_selectedFile == null) { RenamePreview = string.Empty; return; }
                var ext        = _selectedFile.Extension?.ToLowerInvariant() ?? "mp4";
                var resolution = string.Empty;
                var fmt        = _selectedFile.Extension ?? string.Empty;
                var pattern    = IsEpisodeMode ? _settings.TvRenamePattern : _settings.RenamePattern;
                int? episodeEnd = (_settings.DetectEpisodeRange && IsEpisodeMode)
                    ? FilenameParser.ParseEpisodeRange(System.IO.Path.GetFileNameWithoutExtension(_selectedFile.FilePath))
                    : null;
                RenamePreview  = _renameService.Preview(pattern, EditingMetadata, "." + ext, resolution, fmt, episodeEnd);
            });
        }, ct);
    }

    public void FireRenameSucceeded(string oldName, string newName) =>
        RenameSucceeded?.Invoke(this, new RenameEventArgs(oldName, newName));
    public void FireRenameFailed(string name, string error) =>
        RenameFailed?.Invoke(this, new RenameEventArgs(name, string.Empty, error));
    public void FireEmbedSucceeded(string fileName, bool wasRenamed) =>
        EmbedSucceeded?.Invoke(this, new EmbedEventArgs(fileName, wasRenamed));
}
