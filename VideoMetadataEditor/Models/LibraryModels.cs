#if !NO_WPF
using System.Windows.Media.Imaging;
#endif
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VideoMetadataEditor.Models;

// ─── Library Entry (one row in the Library table) ────────────────────────────

public class LibraryEntry : INotifyPropertyChanged
{
    // ── File identity ──────────────────────────────────────────────────────────
    public string FilePath     { get; set; } = string.Empty;
    public string FileName     => System.IO.Path.GetFileName(FilePath);
    public long   FileSizeBytes { get; set; }
    public string FileSizeDisplay =>
        FileSizeBytes >= 1_073_741_824 ? $"{FileSizeBytes / 1_073_741_824.0:F2} GB"
        : FileSizeBytes >= 1_048_576   ? $"{FileSizeBytes / 1_048_576.0:F1} MB"
        :                                $"{FileSizeBytes / 1024.0:F0} KB";

    // ── Metadata ───────────────────────────────────────────────────────────────
    // ALL displayed fields use notifying setters so the DataGrid refreshes
    // immediately when SyncLibraryEntry updates them after an embed.

    private string _title = string.Empty;
    public  string Title { get => _title; set { if (_title != value) { _title = value; OnPropertyChanged(); } } }

    private string _year = string.Empty;
    public  string Year { get => _year; set { if (_year != value) { _year = value; OnPropertyChanged(); } } }

    private string _genre = string.Empty;
    public  string Genre { get => _genre; set { if (_genre != value) { _genre = value; OnPropertyChanged(); } } }

    private string _director = string.Empty;
    public  string Director { get => _director; set { if (_director != value) { _director = value; OnPropertyChanged(); } } }

    private string _cast = string.Empty;
    public  string Cast { get => _cast; set { if (_cast != value) { _cast = value; OnPropertyChanged(); } } }

    private string _description = string.Empty;
    public  string Description { get => _description; set { if (_description != value) { _description = value; OnPropertyChanged(); } } }

    private string _imdbId = string.Empty;
    public  string ImdbId { get => _imdbId; set { if (_imdbId != value) { _imdbId = value; OnPropertyChanged(); } } }

    private string _tmdbId = string.Empty;
    public  string TmdbId { get => _tmdbId; set { if (_tmdbId != value) { _tmdbId = value; OnPropertyChanged(); } } }

    private float _imdbRating;
    public  float ImdbRating
    {
        get => _imdbRating;
        set { if (Math.Abs(_imdbRating - value) > 0.001f) { _imdbRating = value; OnPropertyChanged(); OnPropertyChanged(nameof(RatingDisplay)); } }
    }
    public string RatingDisplay => ImdbRating > 0 ? $"{ImdbRating:F1}" : string.Empty;

    private string _mpaRating = string.Empty;
    public  string MpaRating
    {
        get => _mpaRating;
        set { if (_mpaRating != value) { _mpaRating = value; OnPropertyChanged(); OnPropertyChanged(nameof(MpaRatingDisplay)); } }
    }
    /// <summary>Display fallback: shows "NR" when no rating is stored, so the column never appears empty.</summary>
    public string MpaRatingDisplay => string.IsNullOrWhiteSpace(_mpaRating) ? "NR" : _mpaRating;

    // ── TV / Episode fields ───────────────────────────────────────────────────
    private bool _isEpisode;
    public  bool IsEpisode { get => _isEpisode; set { if (_isEpisode != value) { _isEpisode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ContentTypeDisplay)); } } }

    private bool _isWatched;
    public  bool IsWatched { get => _isWatched; set { if (_isWatched != value) { _isWatched = value; OnPropertyChanged(); } } }

    public bool   IsMissingEpisode { get; set; }  // synthesised gap-fill — set once at scan time

    // ── Subtitle sidecars ─────────────────────────────────────────────────────
    public List<SubtitleFile> Subtitles     { get; set; } = new();
    public bool               HasSubtitles  => Subtitles.Count > 0;
    public string             SubtitleSummary => Services.SubtitleDetector.BuildSummary(Subtitles);

    /// <summary>
    /// True when this entry was built by a fresh TagLib# read (cache miss or forced read).
    /// Used by the in-place scan merge to know when it's safe to overwrite in-memory
    /// metadata with the scan result. Not persisted — transient per-scan flag.
    /// </summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool IsFreshRead { get; set; }

    /// <summary>
    /// Converts this library entry to a <see cref="Models.MovieMetadata"/> for NFO export
    /// or other metadata write operations.
    /// </summary>
    public Models.MovieMetadata ToMovieMetadata() => new()
    {
        Title        = Title,
        Year         = Year,
        Genre        = Genre,
        Director     = Director,
        Cast         = Cast,
        Description  = Description,
        ImdbId       = ImdbId,
        TmdbId       = TmdbId,
        Rating       = ImdbRating,
        MpaRating    = MpaRating,
        IsEpisode    = IsEpisode,
        ShowTitle    = ShowTitle,
        Season       = Season,
        Episode      = Episode,
        EpisodeTitle = EpisodeTitle,
        AiredDate    = AiredDate,
        IsWatched    = IsWatched,
        ArtworkBytes = CoverArt,
    };
    public string ShowTitle     { get; set; } = string.Empty;
    public int?   Season        { get; set; }
    public int?   Episode       { get; set; }
    public string EpisodeTitle  { get; set; } = string.Empty;
    public string AiredDate     { get; set; } = string.Empty;
    public string TmdbSeriesId  { get; set; } = string.Empty;

    /// <summary>"📺 TV Episode" or "🎬 Movie" — shown in library Content column.</summary>
    public string ContentTypeDisplay => IsEpisode ? "📺 TV" : "🎬 Movie";

    /// <summary>"S01E05" style display — empty for movies.</summary>
    /// <summary>"2" or "" — season number for the Season column.</summary>
    public string SeasonDisplay  => Season.HasValue  ? Season.Value.ToString()  : string.Empty;

    /// <summary>"6" or "" — episode number for the Episode column.</summary>
    public string EpisodeDisplay => Episode.HasValue ? Episode.Value.ToString() : string.Empty;

    public string EpisodeCodeDisplay => IsEpisode && Season.HasValue && Episode.HasValue
        ? $"S{Season:D2}E{Episode:D2}" : string.Empty;
    // CoverArt stores raw bytes; CoverSource is the pre-decoded BitmapSource
    // bound in the DataGrid. Pre-decoding on the scan thread means the UI
    // thread never has to run BytesToImageSourceConverter in a hot path,
    // allowing row virtualization to be safely re-enabled.
    private byte[]? _coverArt;
    public byte[]? CoverArt
    {
        get => _coverArt;
        set
        {
            _coverArt = value;
            OnPropertyChanged();
#if !NO_WPF
            // Pre-decode to BitmapSource so the DataGrid binding is instant
            CoverSource = DecodeCoverArt(value);
            OnPropertyChanged(nameof(CoverSource));
#endif
        }
    }

#if !NO_WPF
    private BitmapSource? _coverSource;
    public BitmapSource? CoverSource
    {
        get => _coverSource;
        private set { _coverSource = value; OnPropertyChanged(); }
    }

    private static BitmapSource? DecodeCoverArt(byte[]? bytes)
    {
        if (bytes is not { Length: > 0 }) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource   = new System.IO.MemoryStream(bytes);
            bmp.CacheOption    = BitmapCacheOption.OnLoad;
            bmp.DecodePixelHeight = 52; // match DataGrid RowHeight — no larger needed
            bmp.EndInit();
            bmp.Freeze();           // cross-thread safe, prevents further allocations
            return bmp;
        }
        catch { return null; }
    }
#endif

    // ── Technical ─────────────────────────────────────────────────────────────
    public string Duration        { get; set; } = string.Empty;   // e.g. "1:52:44"
    public string VideoCodec      { get; set; } = string.Empty;
    public string Resolution      { get; set; } = string.Empty;
    public string AudioCodec      { get; set; } = string.Empty;
    public string Format          { get; set; } = string.Empty;

    // ── File system ───────────────────────────────────────────────────────────
    public DateTime? DownloadDate { get; set; }
    public string DownloadDateDisplay =>
        DownloadDate.HasValue ? DownloadDate.Value.ToString("yyyy-MM-dd") : string.Empty;

    // ── UI state ──────────────────────────────────────────────────────────────
    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        set { if (_isScanning != value) { _isScanning = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ─── Column definition for the Library DataGrid ───────────────────────────────

public class LibraryColumn : INotifyPropertyChanged
{
    public string  Id           { get; set; } = string.Empty;   // unique key
    public string  Binding      { get; set; } = string.Empty;   // property name on LibraryEntry
    public double  Width        { get; set; } = 120;
    public int     DisplayIndex { get; set; }

    private string _header = string.Empty;
    public string Header
    {
        get => _header;
        set { if (_header != value) { _header = value; OnPropertyChanged(); } }
    }

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set { if (_isVisible != value) { _isVisible = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}


// ─── Library Tab model ────────────────────────────────────────────────────────

#if !NO_WPF
/// <summary>
/// Represents one pane in the Library tab when multiple folders are added.
/// Each pane has its own CollectionViewSource for independent filtering/sorting.
/// </summary>
public class LibraryTab : VideoMetadataEditor.ViewModels.ViewModelBase
{
    public string FolderPath  { get; }
    public string Header      => System.IO.Path.GetFileName(FolderPath.TrimEnd('\\', '/')) is { Length: > 0 } s ? s : FolderPath;
    public string ToolTipPath => FolderPath;

    // Entries belonging ONLY to this folder (subset of the master LibraryEntries list)
    public System.Collections.ObjectModel.ObservableCollection<LibraryEntry> Entries { get; } = new();

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    /// <summary>
    /// User-overridable TV flag. Null = auto-detect from path/content.
    /// True/False = explicit user override (persisted in Settings.TabTypeOverrides).
    /// </summary>
    private bool? _isTvOverride;
    public bool? IsTvOverride { get => _isTvOverride; set => Set(ref _isTvOverride, value); }

    // Independent CollectionViewSource — filter/sort don't affect other tabs
    private readonly System.Windows.Data.CollectionViewSource _cvs;
    public System.ComponentModel.ICollectionView View => _cvs.View;

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set
        {
            Set(ref _filterText, value);
            _cvs.View.Refresh();
            RaiseProperty(nameof(FilterCount));
        }
    }

    public string FilterCount => _filterText.Length > 0
        ? $"{_cvs.View.Cast<object>().Count()}/{Entries.Count}"
        : string.Empty;

    public LibraryTab(string folderPath)
    {
        FolderPath = folderPath;
        _cvs       = new System.Windows.Data.CollectionViewSource { Source = Entries };
        _cvs.View.Filter = obj =>
        {
            if (string.IsNullOrWhiteSpace(_filterText)) return true;
            if (obj is not LibraryEntry e) return false;
            var f = _filterText.Trim();
            return e.Title.Contains(f, System.StringComparison.OrdinalIgnoreCase)
                || e.Genre.Contains(f, System.StringComparison.OrdinalIgnoreCase)
                || e.Director.Contains(f, System.StringComparison.OrdinalIgnoreCase)
                || e.Cast.Contains(f, System.StringComparison.OrdinalIgnoreCase)
                || e.Year.Contains(f, System.StringComparison.OrdinalIgnoreCase)
                || e.ShowTitle.Contains(f, System.StringComparison.OrdinalIgnoreCase);
        };
    }
}
#endif
