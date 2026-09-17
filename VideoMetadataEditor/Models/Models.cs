using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VideoMetadataEditor.Models;

// ─── Write Status (controls file-row colour in the FILES panel) ───────────────

public enum WriteStatus
{
    Untouched, // not yet processed this session — default colour
    Success,   // write + rename succeeded — light green
    Failed     // write failed after all retries — light pink/red
}

// ─── App Settings ────────────────────────────────────────────────────────────

public class AppSettings
{
    public string TmdbApiKey        { get; set; } = string.Empty;
    public string OmdbApiKey        { get; set; } = string.Empty;
    public string OpenSubtitlesApiKey { get; set; } = string.Empty;
    public bool   OpenSubsKeyValidated { get; set; } = false;
    public string OpenSubsLastLanguages { get; set; } = "en";  // persisted search langs
    public string TraktClientId     { get; set; } = string.Empty;
    public string TraktClientSecret { get; set; } = string.Empty;
    public string TraktAccessToken  { get; set; } = string.Empty;  // DPAPI encrypted
    public string TraktRefreshToken { get; set; } = string.Empty;  // DPAPI encrypted
    public string TraktUsername     { get; set; } = string.Empty;
    public long   TraktTokenExpiry  { get; set; } = 0;             // Unix timestamp
    public bool   TraktConnected    { get; set; } = false;
    public bool   TraktSyncOnMark   { get; set; } = true;
    public bool   TraktSyncOnScan   { get; set; } = true;
    public string TraktLastSync     { get; set; } = string.Empty;
    /// <summary>When true, AniList is included as a fallback metadata source for anime content.</summary>
    public bool   UseAniList       { get; set; } = false;
    public bool   LibraryTabbedMode    { get; set; } = false;
    /// <summary>When true, the app checks GitHub Releases for a newer version on startup and shows an update badge if one exists.</summary>
    public bool   CheckForUpdatesOnStartup { get; set; } = true;
    /// <summary>When true, closing/minimising the window hides it to the system tray and the app keeps watching in the background. Exit from the tray menu quits for real.</summary>
    public bool   MinimizeToTray           { get; set; } = false;
    /// <summary>When true, after a successful rename the Files panel drops any leftover
    /// entries whose file no longer exists on disk (e.g. a watch-detected entry for a
    /// name that was then auto-renamed). Purely a display cleanup; never touches files
    /// or entries that still hold undo state. Default on.</summary>
    public bool   AutoCleanStalePanelEntries { get; set; } = true;
    public bool   LibraryTvTreeView    { get; set; } = false;
    public string LanguageCode          { get; set; } = "System";
    // "List" = flat DataGrid (default), "Tree" = Show>Season>Episode hierarchy
    public string TvLibraryViewMode    { get; set; } = "List";
    // IETF language tag for metadata API requests (e.g. "en-US", "ko-KR", "ja-JP")
    public string MetadataLanguage     { get; set; } = "en-US";
    // IETF language tag for UI number/date formatting. Empty = use system culture.
    public string UILanguage           { get; set; } = string.Empty;
    public bool   IsDarkTheme { get; set; } = true;
    public bool   ShowSplashScreen { get; set; } = true;

    // Rename
    public string RenamePattern   { get; set; } = "{Title} ({Year})";
    public string TvRenamePattern { get; set; } = "{ShowTitle} - S{Season}E{Episode} - {EpisodeTitle}";
    // When true, the rename engine inspects the SOURCE filename for a multi-episode
    // range (e.g. S01E01-E03) and expands {Episode} to "01-03". Off by default —
    // single-episode naming is unchanged when disabled.
    public bool   DetectEpisodeRange { get; set; } = false;
    // When true, the {AbsoluteEpisode} token is populated for TV episodes: first by
    // parsing an absolute number from the SOURCE filename (fansub layouts like
    // "[Group] Show - 153"), and if absent, by an AniList relations-graph lookup when
    // the file is AniList-matched. Off by default.
    public bool   AniListAutoDetectAbsolute { get; set; } = false;
    // When true, exporting episode NFOs also writes a tvshow.nfo into each episode's
    // show folder (once per folder). Off by default.
    public bool   WriteTvShowNfo { get; set; } = false;
    // Artwork sidecar export. ExportArtworkSidecars enables the "Export Artwork" action;
    // ArtworkNamingStyle: 0 = Kodi (<video>-poster.jpg), 1 = Plex/Jellyfin (poster.jpg).
    public bool   ExportArtworkSidecars { get; set; } = false;
    public int    ArtworkNamingStyle    { get; set; } = 0;
    public List<string> RenamePresets { get; set; } = new()
    {
        "{Title} ({Year})",
        "{Title}.{Year}",
        "{Title} ({Year}) [{ImdbId}]",
        "{Year} - {Title}"
    };

    // Batch / processing
    public int  MaxConcurrentProcessing { get; set; } = 4;
    /// <summary>When true, batch write concurrency is tuned automatically to the drive and CPU.</summary>
    public bool AutoConcurrentProcessing { get; set; } = true;
    public int  ArtworkMaxPx  { get; set; } = 500;
    public int  ArtworkJpegQuality { get; set; } = 85;

    // Last-used paths
    public string LastFolderPath { get; set; } = string.Empty;
    public string LastFilePath   { get; set; } = string.Empty;

    // Copy engine
    public string PreferredCopyEngine { get; set; } = "Auto";
    public int    CopyMaxRetries   { get; set; } = 3;
    public int    CopyRetryDelayMs { get; set; } = 500;

    // API key lock state
    public bool TmdbKeyValidated { get; set; } = false;
    public bool OmdbKeyValidated { get; set; } = false;

    // UX preferences
    public bool AutoSaveSettings    { get; set; } = true;
    public bool AutoSwitchToResults { get; set; } = true;
    public int  AutoSearchDebounceMs { get; set; } = 300;
    // Write diagnostics
    public bool VerboseWriteErrors  { get; set; } = false;

    // Full diagnostic dump: when ON, every embed writes a detailed per-file report to
    // %TEMP%\vme_artdump\<name>.diag.txt (atom layout, tag state before/after Save,
    // intended comment, compressed-artwork validity, at each pipeline stage). Default
    // OFF — this is a troubleshooting aid for hard-to-diagnose write failures.
    public bool EnableDiagnosticDump { get; set; } = false;

    // External fallback application for write/rename failures (e.g. TagScanner, MediaInfo)
    // Launched with the failed file path as the first argument.
    public string FallbackAppPath   { get; set; } = string.Empty;

    // Watch folder
    public bool   WatchFolderEnabled        { get; set; } = false;
    public int    WatchFolderPollMinutes    { get; set; } = 3;
    public bool   WatchFolderRecursive      { get; set; } = true;
    public bool   AddFolderRecursive        { get; set; } = true;
    public bool   AutoEmbedEnabled          { get; set; } = false;
    // Auto-rename after embed: true=always rename, false=prompt when type is uncertain
    public bool   AutoRenameEnabled         { get; set; } = true;
    // Prompt when IsEpisode from TMDB doesn't match the parsed filename pattern
    public bool   ConfirmTypeMismatchRename  { get; set; } = true;
    // Confidence required before auto-embedding: 0=Low, 1=Medium, 2=High, 3=ExactOnly
    public int    AutoEmbedConfidenceLevel  { get; set; } = 2;
    public string LastAutoEmbedTime         { get; set; } = string.Empty;

    // Library tab
    public string LibraryFolderPath  { get; set; } = string.Empty;
    /// <summary>Additional library folders combined with LibraryFolderPath in one scan.</summary>
    public List<string> ExtraLibraryFolders { get; set; } = new();
    /// <summary>Key=FolderPath, Value=true(TV)/false(Movie) — user tab type override.</summary>
    public Dictionary<string, bool> TabTypeOverrides { get; set; } = new();
    public bool   LibraryRecursive   { get; set; } = true;
    public List<LibraryColumnSettings> LibraryColumns   { get; set; } = new();
    public List<LibraryColumnSettings> MovieColumns   { get; set; } = new();
    public List<LibraryColumnSettings> TvColumns      { get; set; } = new();
    public bool   LibraryWatchEnabled { get; set; } = false;
    public int    LibraryWatchPollMinutes { get; set; } = 5;

    // Move/Copy tab persistent settings
    public string LastCopyDestination  { get; set; } = string.Empty;
    public string LastConflictMode     { get; set; } = "Skip";

    // Smart Move/Copy — organise by content type
    public bool   SmartOrganiseEnabled   { get; set; } = false;
    public string MoviesFolderName       { get; set; } = "Movies";
    public string TvShowsFolderName      { get; set; } = "TV Shows";
    public bool   CreateSeasonSubfolders { get; set; } = true;

    // How to handle files with no metadata during Smart Organise.
    // "SendToMovies"   — drop into Movies folder (legacy behaviour)
    // "SendToUnsorted" — drop into a dedicated Unsorted folder (recommended)
    // "Skip"           — exclude from the copy/move entirely
    public string UntaggedFileHandling { get; set; } = "SendToUnsorted";
    public string UnsortedFolderName   { get; set; } = "Unsorted";
    
    // Orphan temp file policy
    // "Ask"        — show RecoveryDialog when orphans detected (default)
    // "AutoDelete" — silently delete all orphans without prompting
    // "Ignore"     — skip orphan scan entirely
    public string OrphanTempFilePolicy { get; set; } = "Ask";

    // Duplicates tab
    public bool   ShowDuplicatesTab          { get; set; } = false;
    public string DuplicateSourceFolder      { get; set; } = string.Empty;
    public string DuplicateDestinationFolder { get; set; } = string.Empty;
    public bool   DuplicateMoveInsteadOfDelete { get; set; } = false;
    public bool   DuplicateIncludeSubfolders    { get; set; } = true;
    public string DuplicateExcludedPatterns     { get; set; } = "*-sample.*\n*\\Trailers\\*\n*\\Featurettes\\*\n*\\Extras\\*";

    // Media Health Check tab — remember last scanned folder + recurse preference
    public string MediaHealthFolder    { get; set; } = string.Empty;
    public bool   MediaHealthRecursive { get; set; } = true;
}

// ─── Movie Metadata ───────────────────────────────────────────────────────────

public class MovieMetadata : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _year = string.Empty;
    private string _description = string.Empty;
    private string _genre = string.Empty;
    private string _director = string.Empty;
    private string _cast = string.Empty;
    private string _imdbId = string.Empty;
    private string _tmdbId = string.Empty;
    private string _posterUrl = string.Empty;
    private byte[]? _artworkBytes;
    private float _rating;
    private int   _ratingVotes;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Year { get => _year; set => Set(ref _year, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string Genre { get => _genre; set => Set(ref _genre, value); }
    public string Director { get => _director; set => Set(ref _director, value); }
    public string Cast { get => _cast; set => Set(ref _cast, value); }
    public string ImdbId { get => _imdbId; set => Set(ref _imdbId, value); }
    public string TmdbId { get => _tmdbId; set => Set(ref _tmdbId, value); }
    public string PosterUrl { get => _posterUrl; set => Set(ref _posterUrl, value); }
    public byte[]? ArtworkBytes { get => _artworkBytes; set => Set(ref _artworkBytes, value); }

    /// <summary>IMDB rating 0.0–10.0. Stored in the tag's popularimeter/rating field.</summary>
    public float Rating
    {
        get => _rating;
        set
        {
            if (Set(ref _rating, Math.Clamp(value, 0f, 10f)))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StarDisplay)));
        }
    }

    /// <summary>Number of IMDB votes (informational, not embedded in file).</summary>
    public int RatingVotes { get => _ratingVotes; set => Set(ref _ratingVotes, value); }

    /// <summary>
    /// MPA content rating (G, PG, PG-13, R, NC-17, NR, Not Rated).
    /// Sourced from TMDB release_dates or OMDB Rated field.
    /// Stored in the tag Comment field as [VME:MPA=PG-13].
    /// </summary>
    private string _mpaRating = string.Empty;
    public string MpaRating
    {
        get => _mpaRating;
        set
        {
            if (Set(ref _mpaRating, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MpaRatingDisplay)));
        }
    }

    private bool _isWatched;
    /// <summary>Persisted in tag comment as [VME:WATCHED=1].</summary>
    public bool IsWatched { get => _isWatched; set => Set(ref _isWatched, value); }

    // ── TV / Episode fields ───────────────────────────────────────────────────
    /// <summary>True when this file is a TV episode rather than a movie.</summary>
    private bool _isEpisode;
    public bool IsEpisode
    {
        get => _isEpisode;
        set { if (Set(ref _isEpisode, value)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentTypeDisplay))); }
    }

    /// <summary>Series/show title (e.g. "Batman: The Animated Series").</summary>
    public string ShowTitle { get => _showTitle; set => Set(ref _showTitle, value); }
    private string _showTitle = string.Empty;

    /// <summary>Season number (1-based).</summary>
    public int? Season { get => _season; set => Set(ref _season, value); }
    private int? _season;

    /// <summary>Episode number within the season (1-based).</summary>
    public int? Episode { get => _episode; set => Set(ref _episode, value); }
    private int? _episode;

    /// <summary>Individual episode title (e.g. "On Leather Wings").</summary>
    public string EpisodeTitle { get => _episodeTitle; set => Set(ref _episodeTitle, value); }
    private string _episodeTitle = string.Empty;

    /// <summary>Original air date ISO-8601 (e.g. "1992-09-05").</summary>
    public string AiredDate { get => _airedDate; set => Set(ref _airedDate, value); }
    private string _airedDate = string.Empty;

    /// <summary>TVDB series ID (optional, for Kodi/Jellyfin NFO).</summary>
    public string TvdbId { get => _tvdbId; set => Set(ref _tvdbId, value); }
    private string _tvdbId = string.Empty;

    /// <summary>TMDB series ID (distinct from movie TMDB ID).</summary>
    public string TmdbSeriesId { get => _tmdbSeriesId; set => Set(ref _tmdbSeriesId, value); }
    private string _tmdbSeriesId = string.Empty;

    /// <summary>AniList media ID (anime). Retained so {AbsoluteEpisode} can use the
    /// AniList relations-graph fallback at rename time. Empty for non-AniList sources.</summary>
    public string AniListId { get => _aniListId; set => Set(ref _aniListId, value); }
    private string _aniListId = string.Empty;

    /// <summary>Displayed in the mode toggle: "Movie" or "TV Episode".</summary>
    public string ContentTypeDisplay => _isEpisode ? "TV Episode" : "Movie";

    /// <summary>Formatted MPA rating for display — empty string when not set.</summary>
    public string MpaRatingDisplay => string.IsNullOrWhiteSpace(_mpaRating) ? "NR" : _mpaRating;

    /// <summary>Visual star representation: ★★★½☆ 7.1/10 style.</summary>
    public string StarDisplay
    {
        get
        {
            if (_rating <= 0f) return string.Empty;
            float half = _rating / 2f;
            int full = (int)Math.Floor(half);
            float frac = half - full;
            bool addHalf = frac >= 0.25f && frac < 0.75f;
            if (frac >= 0.75f) full++;
            int empty = 5 - full - (addHalf ? 1 : 0);
            return new string('★', full) + (addHalf ? "½" : "") + new string('☆', empty)
                   + $"  {_rating:F1}/10"
                   + (_ratingVotes > 0 ? $"  ({_ratingVotes:N0} votes)" : "");
        }
    }

    // Source links for Retrieved Data tab
    public string? TmdbUrl { get; set; }
    public string? ImdbUrl { get; set; }
    public string? OmdbUrl { get; set; }

    public MovieMetadata Clone() => new()
    {
        Title = Title, Year = Year, Description = Description,
        Genre = Genre, Director = Director, Cast = Cast,
        ImdbId = ImdbId, TmdbId = TmdbId, PosterUrl = PosterUrl,
        ArtworkBytes = ArtworkBytes, TmdbUrl = TmdbUrl,
        ImdbUrl = ImdbUrl, OmdbUrl = OmdbUrl,
        Rating = Rating, RatingVotes = RatingVotes, MpaRating = MpaRating,
            IsEpisode = IsEpisode, ShowTitle = ShowTitle, Season = Season,
            Episode = Episode, EpisodeTitle = EpisodeTitle, AiredDate = AiredDate,
            TvdbId = TvdbId, TmdbSeriesId = TmdbSeriesId, AniListId = AniListId, IsWatched = IsWatched
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}

// ─── Video File ───────────────────────────────────────────────────────────────

public enum VideoFormat { MP4, MKV, MOV, WMV, M4V, WebM, AVI, Unknown }

public class VideoFile : INotifyPropertyChanged
{
    private bool _isSelected = true;
    private string _statusMessage = string.Empty;
    private bool _isProcessing;
    private bool _isDone;
    private bool _hasError;
    private string _filePath = string.Empty;

    public string FilePath
    {
        get => _filePath;
        set
        {
            if (_filePath == value) return;
            _filePath = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilePath)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FileNameNoExt)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Extension)));
        }
    }
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public string FileNameNoExt => System.IO.Path.GetFileNameWithoutExtension(FilePath);
    public string Extension => System.IO.Path.GetExtension(FilePath).TrimStart('.').ToUpperInvariant();
    public VideoFormat Format { get; init; }
    public long FileSizeBytes { get; init; }
    public string FileSizeDisplay => FormatSize(FileSizeBytes);

    // ── Remux candidate state ───────────────────────────────────────────────
    // When this file is an un-committed remux candidate (e.g. Movie.remux.mp4),
    // IsRemuxCandidate=true, and OriginalPath points at the untouched original
    // (Movie.mp4). The Preview panel shows Replace/Restore buttons for these.

    private bool _isRemuxCandidate;
    public bool IsRemuxCandidate
    {
        get => _isRemuxCandidate;
        set
        {
            if (_isRemuxCandidate == value) return;
            _isRemuxCandidate = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRemuxCandidate)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RemuxBadge)));
        }
    }

    private string _originalPath = string.Empty;
    /// <summary>For a remux candidate, the path of the untouched original file.</summary>
    public string OriginalPath
    {
        get => _originalPath;
        set { _originalPath = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OriginalPath))); }
    }

    public string RemuxBadge => IsRemuxCandidate ? "REMUXED" : string.Empty;

    // ── Read-only / lock state ────────────────────────────────────────────────

    /// <summary>Reads the read-only attribute directly from disk. Refreshes on demand.</summary>
    // IsReadOnly: cached so WPF binding doesn't hit the filesystem on every render.
    // Cache is invalidated by TrySetReadOnly and by RefreshReadOnly().
    private bool? _isReadOnlyCached;
    public bool IsReadOnly
    {
        get
        {
            if (_isReadOnlyCached.HasValue) return _isReadOnlyCached.Value;
            try { _isReadOnlyCached = new System.IO.FileInfo(FilePath).IsReadOnly; }
            catch { _isReadOnlyCached = false; }
            return _isReadOnlyCached.Value;
        }
    }

    /// <summary>Forces a re-read of the read-only attribute from disk.</summary>
    public void RefreshReadOnly()
    {
        _isReadOnlyCached = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReadOnly)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LockIcon)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LockToolTip)));
    }

    /// <summary>Sets or clears the read-only attribute on disk and fires change notification.</summary>
    public bool TrySetReadOnly(bool readOnly)
    {
        try
        {
            new System.IO.FileInfo(FilePath).IsReadOnly = readOnly;
            _isReadOnlyCached = readOnly; // update cache immediately
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsReadOnly)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LockIcon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LockToolTip)));
            return true;
        }
        catch { return false; }
    }

    public string LockIcon    => IsReadOnly ? "🔒" : "🔓";
    public string LockToolTip => IsReadOnly
        ? "File is read-only — click to unlock for editing"
        : "File is writable — click to lock against changes";

    // Filename parse results
    public string ParsedTitle { get; set;  } = string.Empty;
    public string ParsedYear  { get; set;  } = string.Empty;

    // Metadata states
    public MovieMetadata EmbeddedMetadata { get; set; } = new();
    public MovieMetadata? RetrievedMetadata { get; set; }

    // ── Subtitle sidecars (detected on file load) ─────────────────────────────
    public System.Collections.Generic.List<SubtitleFile> Subtitles { get; set; } = new();
    public bool HasSubtitles => Subtitles.Count > 0;
    public MovieMetadata PendingMetadata { get; set; } = new();

    // Format capability flags
    public bool SupportsArtwork { get; init; } = true;
    public bool SupportsFullTags { get; init; } = true;
    public string? FormatWarning { get; init; }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }
    public bool IsProcessing { get => _isProcessing; set => Set(ref _isProcessing, value); }
    public bool IsDone { get => _isDone; set => Set(ref _isDone, value); }
    public bool HasError { get => _hasError; set => Set(ref _hasError, value); }

    // ── Session write status (controls row colour) ────────────────────────────

    private WriteStatus _writeStatus = WriteStatus.Untouched;
    public WriteStatus WriteStatus
    {
        get => _writeStatus;
        set
        {
            Set(ref _writeStatus, value);
            // Sync legacy flags used elsewhere
            IsDone     = value == WriteStatus.Success;
            HasError   = value == WriteStatus.Failed;
        }
    }

    private string _writeErrorDetail = string.Empty;
    /// <summary>Human-readable explanation of the last write failure (populated when VerboseWriteErrors is on).</summary>
    public string WriteErrorDetail { get => _writeErrorDetail; set => Set(ref _writeErrorDetail, value); }

    /// <summary>
    /// True when, after an embed, the file was re-read from disk and the embedded
    /// metadata matched what was intended. This is the "what's actually on disk"
    /// signal: a green check means the panel reflects verified disk content, not just
    /// an in-memory snapshot that might silently revert on the next scan. Null =
    /// not yet embedded this session; true = verified; false = written but the
    /// disk re-read did not match (a warning state).
    /// </summary>
    private bool? _diskVerified;
    public bool? DiskVerified
    {
        get => _diskVerified;
        set { Set(ref _diskVerified, value); OnPropertyChanged(nameof(DiskVerifiedDisplay)); }
    }

    /// <summary>Short badge text for the FILES panel verification indicator.</summary>
    public string DiskVerifiedDisplay => _diskVerified switch
    {
        true  => "✓ verified on disk",
        false => "⚠ not verified",
        _     => string.Empty,
    };

    // Watched flag — stored in VME custom tag
    private bool _isWatched;
    public bool IsWatched { get => _isWatched; set => Set(ref _isWatched, value); }

    // Watch-folder: true when file was discovered by the background watcher (not by manual Add)
    private bool _isNewFile;
    public bool IsNewFile { get => _isNewFile; set => Set(ref _isNewFile, value); }

    // Separator row: a blank divider row between groups of files from different sources
    public bool IsSeparator { get; set; } = false;

    // Auto-search match confidence 0-100 (0 = not searched, -1 = no match)
    private int _matchScore = 0;
    public int MatchScore { get => _matchScore; set => Set(ref _matchScore, value); }

    // Match label displayed in file list
    public string MatchLabel => MatchScore switch
    {
        100 => "✓ Exact",
        > 80 => "≈ Good",
        > 50 => "~ Partial",
        0    => string.Empty,
        _    => "? Weak"
    };

    // Undo snapshot — original filename + original metadata before last embed
    public string? UndoFilePath { get; set; }
    public MovieMetadata? UndoMetadata { get; set; }
    public bool CanUndo => UndoFilePath != null || UndoMetadata != null;

    /// <summary>Trakt watch progress 0–100. 0 = not started, 100 = complete.
    /// Populated after a Trakt sync; displayed as a thin progress bar in the Files panel.</summary>
    public int WatchedProgress
    {
        get => _watchedProgress;
        set
        {
            if (_watchedProgress == value) return;
            _watchedProgress = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(WatchedProgress)));
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ProgressLabel)));
        }
    }
    private int _watchedProgress;
    public string ProgressLabel => WatchedProgress switch
    {
        0   => string.Empty,
        100 => "✓ Complete",
        _   => $"{WatchedProgress}%"
    };

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// ─── API Search Result ────────────────────────────────────────────────────────

public class SearchResult
{
    public string Title { get; set; } = string.Empty;
    public string Year { get; set; } = string.Empty;
    /// <summary>"📺" for TV shows, "🎬" for movies, empty for AniList anime.</summary>
    public string TypeIcon => Source == "TMDB_TV" ? "📺" : Source == "AniList" ? "🎌" : "🎬";
    public string ImdbId { get; set; } = string.Empty;
    public string TmdbId { get; set; } = string.Empty;
    public string AniListId { get; set; } = string.Empty;
    public string PosterUrl { get; set; } = string.Empty;
    public string Overview { get; set; } = string.Empty;
    public string DisplayText => $"{TypeIcon} {Title} ({Year})";
    /// <summary>Identifies which API produced this result for the detail-fetch call.</summary>
    public string Source { get; set; } = "TMDB"; // "TMDB", "OMDB", "AniList"
    /// <summary>TMDB popularity score — used as a tiebreaker in result ranking.</summary>
    public double Popularity { get; set; }
}

// ─── Library Column persistence ───────────────────────────────────────────────

public class LibraryColumnSettings
{
    public string Id           { get; set; } = string.Empty;
    public bool   IsVisible    { get; set; } = true;
    public int    DisplayIndex { get; set; }
    public double Width        { get; set; } = 120;
}

// ─── TV Episode picker item ────────────────────────────────────────────────────

/// <summary>Lightweight episode record used in the Season/Episode picker ComboBox.</summary>
public class TvEpisodeItem
{
    public int    EpisodeNumber { get; set; }
    public string Title        { get; set; } = string.Empty;
    public string AirDate      { get; set; } = string.Empty;
    public string Overview     { get; set; } = string.Empty;

    /// <summary>Display text in the ComboBox: "E05 · On Leather Wings"</summary>
    public string DisplayText => $"E{EpisodeNumber:D2} · {Title}";
}
