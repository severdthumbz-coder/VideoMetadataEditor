using System.Collections.ObjectModel;
using System.Windows.Input;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;

namespace VideoMetadataEditor.ViewModels;

/// <summary>
/// Owns all search, retrieval, and TV picker state.
/// Replaces the scattered search/TV state that previously lived in MainViewModel.
/// </summary>
public class SearchViewModel : ViewModelBase
{
    private readonly ApiService          _api;
    private readonly AniListApiService   _aniList;
    private readonly Func<string>        _tmdbKey;
    private readonly Func<string>        _omdbKey;
    private readonly Action<MovieMetadata?> _onMetadataReady;

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action<MovieMetadata>? EpisodeApplied;

    // ── Search state ──────────────────────────────────────────────────────────
    private string _query = string.Empty;
    public string Query
    {
        get => _query;
        set { Set(ref _query, value); }
    }

    public ObservableCollection<SearchResult> Results { get; } = new();

    private SearchResult? _selectedResult;
    public SearchResult? SelectedResult
    {
        get => _selectedResult;
        set { Set(ref _selectedResult, value); }
    }

    private MovieMetadata? _retrievedMetadata;
    public MovieMetadata? RetrievedMetadata
    {
        get => _retrievedMetadata;
        set
        {
            Set(ref _retrievedMetadata, value);
            RaiseProperty(nameof(HasTvEpisodeResult));
        }
    }

    public bool HasTvEpisodeResult =>
        RetrievedMetadata?.IsEpisode == true && !IsTvPickerVisible;

    private bool _isSearching;
    public bool IsSearching { get => _isSearching; private set => Set(ref _isSearching, value); }

    // ── TV picker state ───────────────────────────────────────────────────────
    private bool _isTvPickerVisible;
    public bool IsTvPickerVisible
    {
        get => _isTvPickerVisible;
        set { Set(ref _isTvPickerVisible, value); RaiseProperty(nameof(HasTvEpisodeResult)); }
    }

    public ObservableCollection<int>          TvSeasonNumbers { get; } = new();
    public ObservableCollection<TvEpisodeItem> TvEpisodeItems { get; } = new();

    private int _tvSelectedSeason = 1;
    private bool _suppressSeasonAutoLoad;
    public int TvSelectedSeason
    {
        get => _tvSelectedSeason;
        set
        {
            Set(ref _tvSelectedSeason, value);
            if (value > 0 && !_suppressSeasonAutoLoad)
                _ = LoadTvSeasonEpisodesAsync(value);
        }
    }

    private TvEpisodeItem? _tvSelectedEpisode;
    public TvEpisodeItem? TvSelectedEpisode
    {
        get => _tvSelectedEpisode;
        set
        {
            Set(ref _tvSelectedEpisode, value);
            RaiseProperty(nameof(CanPickTvEpisode));
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Normal,
                new Action(CommandManager.InvalidateRequerySuggested));
        }
    }

    private string _tvPickerShowTitle = string.Empty;
    public string TvPickerShowTitle
    {
        get => _tvPickerShowTitle;
        private set => Set(ref _tvPickerShowTitle, value);
    }

    public bool CanPickTvEpisode =>
        _tvSelectedEpisode != null && !string.IsNullOrEmpty(_selectedSeriesTmdbId);

    private string _selectedSeriesTmdbId = string.Empty;
    private CancellationTokenSource? _tvPickerCts;

    // ── Commands ──────────────────────────────────────────────────────────────
    public ICommand SearchCommand        { get; }
    public ICommand LookupByIdCommand    { get; }
    public ICommand ClearCommand         { get; }
    public ICommand ApplyRetrievedCommand{ get; }
    public ICommand PickTvEpisodeCommand { get; }
    public ICommand HideTvPickerCommand  { get; }
    public ICommand LoadMoreCommand      { get; }

    // Action delegates set by MainViewModel for cross-VM operations
    public Action<MovieMetadata>? OnApplyRetrieved { get; set; }

    public SearchViewModel(
        ApiService api,
        AniListApiService aniList,
        Func<string> tmdbKey,
        Func<string> omdbKey,
        Action<MovieMetadata?> onMetadataReady)
    {
        _api             = api;
        _aniList         = aniList;
        _tmdbKey         = tmdbKey;
        _omdbKey         = omdbKey;
        _onMetadataReady = onMetadataReady;

        SearchCommand         = new AsyncRelayCommand(SearchAsync,
            _ => !string.IsNullOrWhiteSpace(Query) && !IsSearching);
        LookupByIdCommand     = new AsyncRelayCommand(LookupAsync,
            _ => !IsSearching);
        ClearCommand          = new RelayCommand(_ => Clear());
        ApplyRetrievedCommand = new RelayCommand(
            _ => { if (RetrievedMetadata != null) OnApplyRetrieved?.Invoke(RetrievedMetadata); },
            _ => RetrievedMetadata != null);
        PickTvEpisodeCommand  = new AsyncRelayCommand(PickTvEpisodeAsync,
            _ => CanPickTvEpisode);
        HideTvPickerCommand   = new RelayCommand(_ => HidePicker());
        LoadMoreCommand       = new AsyncRelayCommand(LoadMoreAsync);
    }

    private void Clear()
    {
        Results.Clear();
        SelectedResult    = null;
        RetrievedMetadata = null;
        IsTvPickerVisible = false;
    }

    private void HidePicker()
    {
        IsTvPickerVisible = false;
        TvSeasonNumbers.Clear();
        TvEpisodeItems.Clear();
        TvSelectedEpisode = null;
        _selectedSeriesTmdbId = string.Empty;
    }

    public void ResetForNewFile()
    {
        _tvPickerCts?.Cancel();
        _tvPickerCts = null;
        IsTvPickerVisible     = false;
        TvSeasonNumbers.Clear();
        TvEpisodeItems.Clear();
        TvSelectedEpisode     = null;
        _selectedSeriesTmdbId = string.Empty;
        _selectedSeriesTmdbId = string.Empty;
    }

    // ── Search ────────────────────────────────────────────────────────────────
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(Query)) return;
        IsSearching = true;
        Results.Clear();
        RetrievedMetadata = null;
        try
        {
            var (tmdbResults, _) = await _api.SearchTmdbAsync(Query, _tmdbKey());
            var (omdbResults, _) = await _api.SearchOmdbAsync(Query, _omdbKey());
            var results = (tmdbResults ?? new()).Concat(omdbResults ?? new()).ToList();
            foreach (var r in results) Results.Add(r);
        }
        finally { IsSearching = false; }
    }

    private async Task LookupAsync()
    {
        // ID-based lookup delegated — handled by MainViewModel.Search.cs
    }

    private async Task LoadMoreAsync() { }

    // ── TV Picker ─────────────────────────────────────────────────────────────
    public async Task ShowTvPickerAsync(string seriesTmdbId, string showTitle)
    {
        _tvPickerCts?.Cancel();
        _tvPickerCts = new CancellationTokenSource();
        var cts = _tvPickerCts;

        _selectedSeriesTmdbId = seriesTmdbId;
        TvPickerShowTitle     = showTitle;

        try
        {
            var (seasonCount, _, _) = await _api.GetTvSeriesInfoAsync(
                seriesTmdbId, _tmdbKey(), cts.Token);
            if (cts.IsCancellationRequested) return;

            TvSeasonNumbers.Clear();
            for (int i = 1; i <= Math.Max(1, seasonCount); i++)
                TvSeasonNumbers.Add(i);

            int defaultSeason = 1;
            IsTvPickerVisible = true;

            _suppressSeasonAutoLoad = true;
            try { Set(ref _tvSelectedSeason, defaultSeason, nameof(TvSelectedSeason)); }
            finally { _suppressSeasonAutoLoad = false; }

            await LoadTvSeasonEpisodesAsync(defaultSeason);
        }
        catch (OperationCanceledException) { IsTvPickerVisible = false; }
    }

    private async Task LoadTvSeasonEpisodesAsync(int season)
    {
        if (string.IsNullOrEmpty(_selectedSeriesTmdbId)) return;
        var cts = _tvPickerCts;
        if (cts?.IsCancellationRequested == true) return;

        TvEpisodeItems.Clear();
        TvSelectedEpisode = null;

        var (episodes, _) = await _api.GetTvSeasonEpisodesAsync(
            _selectedSeriesTmdbId, season, _tmdbKey(),
            cts?.Token ?? CancellationToken.None);
        if (cts?.IsCancellationRequested == true) return;

        foreach (var ep in episodes ?? Enumerable.Empty<TvEpisodeItem>())
            TvEpisodeItems.Add(ep);
        TvSelectedEpisode = TvEpisodeItems.FirstOrDefault();
    }

    private async Task PickTvEpisodeAsync()
    {
        var seriesId   = _selectedSeriesTmdbId;
        var season     = TvSelectedSeason;
        var epItem     = TvSelectedEpisode;
        var epNum      = epItem?.EpisodeNumber ?? 1;
        var showTitle  = TvPickerShowTitle;

        if (string.IsNullOrEmpty(seriesId) || epItem == null) return;

        var (meta, err) = await _api.GetTvEpisodeAsync(seriesId, season, epNum, _tmdbKey());
        if (meta == null && !string.IsNullOrWhiteSpace(_omdbKey()))
            (meta, _) = await _api.GetOmdbEpisodeAsync(showTitle, season, epNum, _omdbKey());

        if (meta != null)
        {
            meta.TmdbSeriesId = seriesId;
            meta.IsEpisode    = true;
            if (string.IsNullOrWhiteSpace(meta.MpaRating)) meta.MpaRating = "NR";

            RetrievedMetadata = meta;
            IsTvPickerVisible = false;
            EpisodeApplied?.Invoke(meta);
        }
    }
}
