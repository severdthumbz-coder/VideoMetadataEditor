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

// ┌─────────────────────────────────────────────────────────────────────────────┐
// │  MainViewModel.Search                                              
// │  Search — auto-search, manual search, ID lookup, result loading
// └─────────────────────────────────────────────────────────────────────────────┘

/// <remarks>
/// This is a partial class. The full MainViewModel is split across six files
/// for maintainability. All files compile to a single class — behaviour is
/// identical to a single-file implementation.
/// See: MainViewModel.cs (core), MainViewModel.FilesPanel.cs,
///      MainViewModel.CopyMove.cs, MainViewModel.Search.cs,
///      MainViewModel.Embed.cs, MainViewModel.Library.cs
/// </remarks>
public partial class MainViewModel
{
    private async Task AutoSearchAsync(VideoFile targetFile, string title, string year,
                                       bool calledFromBatch = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        if (!calledFromBatch && IsBusy) return;
        // Don't auto-search if the user just manually loaded an episode via the picker
        if (_tvEpisodeManuallyLoaded) { _tvEpisodeManuallyLoaded = false; return; }

        if (!calledFromBatch)
        {
            IsBusy = true;
            StatusText = $"Auto-searching: \"{title}\" {year}…";
        }

        try
        {
            MovieMetadata? meta = null;

            // ── TV Episode auto-search (short-circuits movie pipeline) ─────
            // When a file is detected as a TV episode, use SearchTvAsync to
            // find the show, then GetTvEpisodeAsync to fetch the specific episode.
            // `title` is the show title (set by the caller when IsEpisode is true).
            // Check both EmbeddedMetadata (already-embedded files) and
            // EditingMetadata (fresh files where filename detection fired)
            bool fileIsEpisode = targetFile.EmbeddedMetadata.IsEpisode
                || (targetFile == SelectedFile && EditingMetadata.IsEpisode);
            if (fileIsEpisode && !string.IsNullOrWhiteSpace(Settings.TmdbApiKey))
            {
                var (tvResults, _) = await _apiService.SearchTvAsync(title, TmdbKey, ct);
                var best = tvResults.FirstOrDefault(r =>
                    string.Equals(r.Title, title, StringComparison.OrdinalIgnoreCase))
                    ?? tvResults.FirstOrDefault();

                if (best != null && !string.IsNullOrWhiteSpace(best.TmdbId))
                {
                    int season  = targetFile.EmbeddedMetadata.Season  ?? (targetFile == SelectedFile ? EditingMetadata.Season  ?? 1 : 1);
                    int episode = targetFile.EmbeddedMetadata.Episode ?? (targetFile == SelectedFile ? EditingMetadata.Episode ?? 1 : 1);
                    if (!calledFromBatch)
                        StatusText = $"Fetching S{season:D2}E{episode:D2} of {best.Title}…";

                    var (epMeta, _) = await _apiService.GetTvEpisodeAsync(
                        best.TmdbId, season, episode, TmdbKey, ct);
                    if (epMeta != null)
                    {
                        epMeta.TmdbSeriesId = best.TmdbId;
                        epMeta.IsEpisode    = true;
                        meta                = epMeta;
                    }
                }
                // If TMDB TV search failed, try OMDB episode as fallback
                if (meta == null && !string.IsNullOrWhiteSpace(OmdbKey))
                {
                    int s = targetFile.EmbeddedMetadata.Season  ?? (targetFile == SelectedFile ? EditingMetadata.Season  ?? 1 : 1);
                    int e = targetFile.EmbeddedMetadata.Episode ?? (targetFile == SelectedFile ? EditingMetadata.Episode ?? 1 : 1);
                    var (omdbEp, _) = await _apiService.GetOmdbEpisodeAsync(title, s, e, OmdbKey, ct);
                    if (omdbEp != null) meta = omdbEp;
                }
                if (meta != null) goto ApplyResult;
                // Neither TMDB nor OMDB found it — fall through to movie pipeline as last resort
            }

            // ── 1. TMDB title search ───────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(Settings.TmdbApiKey))
            {
                var query   = FilenameParser.BuildSearchQuery(title, year);
                (List<SearchResult> results, string? _) = await _apiService.SearchTmdbAsync(query, TmdbKey, ct: ct);
                var best    = PickBestResult(results, title, year);
                if (best != null)
                    { var (_m, _) = await _apiService.GetTmdbDetailsAsync(best.TmdbId, TmdbKey,
                        downloadArtwork: !calledFromBatch); meta = _m; }
            }

            // ── 2. OMDB title search (fallback) ────────────────────────────
            if (meta == null && !string.IsNullOrWhiteSpace(OmdbKey))
            {
                var query   = FilenameParser.BuildSearchQuery(title, year);
                (List<SearchResult> results, string? _) = await _apiService.SearchOmdbAsync(query, OmdbKey, ct: ct);
                var best    = PickBestResult(results, title, year);
                if (best != null)
                {
                    var (_m, _) = await _apiService.GetOmdbDetailsAsync(best.ImdbId, OmdbKey);
                    meta = _m;
                    // OMDB limits cast to ~4 actors. If we have an IMDB ID, try TMDB
                    // for a full cast list and substitute it in — TMDB is the richer source.
                    if (meta != null && !string.IsNullOrWhiteSpace(meta.ImdbId)
                        && !string.IsNullOrWhiteSpace(Settings.TmdbApiKey))
                    {
                        var (tmdbEnrich, _) = await _apiService.GetTmdbByImdbIdAsync(
                            meta.ImdbId, TmdbKey, ct);
                        if (tmdbEnrich != null)
                        {
                            if (!string.IsNullOrWhiteSpace(tmdbEnrich.Cast))
                                meta.Cast = tmdbEnrich.Cast;
                            if (!string.IsNullOrWhiteSpace(tmdbEnrich.Director))
                                meta.Director = tmdbEnrich.Director;
                            if (string.IsNullOrWhiteSpace(meta.MpaRating) && !string.IsNullOrWhiteSpace(tmdbEnrich.MpaRating))
                                meta.MpaRating = tmdbEnrich.MpaRating;
                        }
                    }
                }
            }

            // ── 3. TMDB title-only search (without year) ───────────────────
            if (meta == null && !string.IsNullOrWhiteSpace(Settings.TmdbApiKey) && !string.IsNullOrWhiteSpace(year))
            {
                (List<SearchResult> results, string? _) = await _apiService.SearchTmdbAsync(title, TmdbKey);
                var best    = PickBestResult(results, title, year);
                if (best != null)
                    { var (_m, _) = await _apiService.GetTmdbDetailsAsync(best.TmdbId, TmdbKey,
                        downloadArtwork: !calledFromBatch); meta = _m; }
            }

            // ── 4. ID-based refinement if IMDB/TMDB ID already in embedded tags ──
            if (meta == null)
            {
                var existingImdb = targetFile.EmbeddedMetadata.ImdbId;
                var existingTmdb = targetFile.EmbeddedMetadata.TmdbId;

                if (!string.IsNullOrWhiteSpace(existingImdb) && !string.IsNullOrWhiteSpace(Settings.TmdbApiKey))
                    { var (_m, _) = await _apiService.GetTmdbByImdbIdAsync(existingImdb, TmdbKey); meta = _m; }

                if (meta == null && !string.IsNullOrWhiteSpace(existingImdb) && !string.IsNullOrWhiteSpace(OmdbKey))
                    { var (_m, _) = await _apiService.GetOmdbDetailsAsync(existingImdb, OmdbKey); meta = _m; }

                if (meta == null && !string.IsNullOrWhiteSpace(existingTmdb) && !string.IsNullOrWhiteSpace(Settings.TmdbApiKey))
                    { var (_m, _) = await _apiService.GetTmdbDetailsAsync(existingTmdb, TmdbKey,
                        downloadArtwork: !calledFromBatch); meta = _m; }
            }

            // ── 5. AniList fallback (when enabled and TMDB+OMDB both failed) ──
            if (meta == null && Settings.UseAniList)
            {
                var (alResults, _) = await _aniListService.SearchAsync(title, year, ct);
                var alBest = alResults.FirstOrDefault();
                if (alBest != null && !string.IsNullOrWhiteSpace(alBest.AniListId))
                {
                    var (alMeta, _) = await _aniListService.GetDetailsAsync(alBest.AniListId, ct);
                    meta = alMeta;
                    if (meta != null)
                        Log( $"[{DateTime.Now:HH:mm:ss}] ℹ AniList used for: {title}");
                }
            }

            ApplyResult:
            // ── 6. Apply result ────────────────────────────────────────────
            if (meta != null)
            {
                // Default MPA to "NR" when no certification found in either source
                if (string.IsNullOrWhiteSpace(meta.MpaRating))
                    meta.MpaRating = "NR";

                targetFile.RetrievedMetadata = meta;

                // Only update UI if this file is still selected
                if (SelectedFile == targetFile)
                {
                    RetrievedMetadata = meta;

                    // Auto-fill editing fields only if currently empty
                    if (string.IsNullOrWhiteSpace(EditingMetadata.Title))       EditingMetadata.Title       = meta.Title;
                    if (string.IsNullOrWhiteSpace(EditingMetadata.Year))        EditingMetadata.Year        = meta.Year;
                    if (string.IsNullOrWhiteSpace(EditingMetadata.Description)) EditingMetadata.Description = meta.Description;
                    if (string.IsNullOrWhiteSpace(EditingMetadata.Genre))       EditingMetadata.Genre       = meta.Genre;
                    if (string.IsNullOrWhiteSpace(EditingMetadata.Director))    EditingMetadata.Director    = meta.Director;
                    if (string.IsNullOrWhiteSpace(EditingMetadata.Cast))        EditingMetadata.Cast        = meta.Cast;
                    // MPA rating: always take from API — it's not user-entered
                    if (!string.IsNullOrWhiteSpace(meta.MpaRating))             EditingMetadata.MpaRating   = meta.MpaRating;
                    // TV-specific auto-fill
                    if (meta.IsEpisode)
                    {
                        EditingMetadata.IsEpisode = true;
                        if (!string.IsNullOrWhiteSpace(meta.ShowTitle))    EditingMetadata.ShowTitle    = meta.ShowTitle;
                        if (!string.IsNullOrWhiteSpace(meta.EpisodeTitle)) EditingMetadata.EpisodeTitle = meta.EpisodeTitle;
                        if (!string.IsNullOrWhiteSpace(meta.AiredDate))    EditingMetadata.AiredDate    = meta.AiredDate;
                        if (!string.IsNullOrWhiteSpace(meta.TmdbSeriesId)) EditingMetadata.TmdbSeriesId = meta.TmdbSeriesId;
                        if (meta.Season.HasValue)  EditingMetadata.Season  = meta.Season;
                        if (meta.Episode.HasValue) EditingMetadata.Episode = meta.Episode;
                        RaiseProperty(nameof(IsEpisodeMode));
                        RaiseProperty(nameof(IsMovieMode));
                    }
                    if (EditingMetadata.ArtworkBytes == null && meta.ArtworkBytes != null)
                    {
                        EditingMetadata.ArtworkBytes = meta.ArtworkBytes;
                        UpdateArtworkDisplay(meta.ArtworkBytes);
                    }

                    UpdateRenamePreview();
                    if (!calledFromBatch)
                    {
                        if (meta.IsEpisode)
                            StatusText = $"✓ Auto-matched: {meta.ShowTitle} S{meta.Season:D2}E{meta.Episode:D2} — {meta.EpisodeTitle}";
                        else
                            StatusText = $"✓ Auto-matched: {meta.Title} ({meta.Year})";
                    }
                }
            }
            else
            {
                if (SelectedFile == targetFile && !calledFromBatch)
                    StatusText = $"No auto-match found for \"{title}\" — search manually in Retrieved Data tab";
            }
        }
        catch (Exception ex)
        {
            if (SelectedFile == targetFile && !calledFromBatch)
                StatusText = $"Auto-search error: {ex.Message}";
        }
        finally
        {
            if (!calledFromBatch) EndOperation(resetProgress: false);
        }
    }

    /// <summary>
    /// Picks the best search result by scoring title similarity and year match.
    /// </summary>
    private static SearchResult? PickBestResult(List<SearchResult> results, string title, string year)
    {
        if (results.Count == 0) return null;
        if (results.Count == 1) return results[0];

        var titleLower = title.ToLowerInvariant();

        return results
            .Select(r => new
            {
                Result = r,
                Score  = ScoreResult(r, titleLower, year)
            })
            .OrderByDescending(x => x.Score)
            .First()
            .Result;
    }

    private static int ScoreResult(SearchResult r, string titleLower, string year)
    {
        int score = 0;
        var rTitle = r.Title.ToLowerInvariant();

        // Normalise both for comparison: strip articles, punctuation
        var rNorm = NormaliseForScore(rTitle);
        var qNorm = NormaliseForScore(titleLower);

        // Exact match (after normalisation handles "The", punctuation, etc.)
        if (rNorm == qNorm)                           score += 100;
        // Result starts with full query
        else if (rNorm.StartsWith(qNorm))             score += 65;
        // Query starts with result (query is longer — e.g. subtitle included)
        else if (qNorm.StartsWith(rNorm))             score += 55;
        // Result contains query
        else if (rNorm.Contains(qNorm))               score += 40;
        // Query contains result
        else if (qNorm.Contains(rNorm))               score += 35;
        else
        {
            // Word-level: count matching words
            var rWords = rNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var qWords = qNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int matches = rWords.Count(w => qWords.Contains(w));
            if (matches > 0)
                score += Math.Min(25, matches * 8); // up to 25 for word overlap
        }

        // Year match is a strong signal
        if (!string.IsNullOrWhiteSpace(year) && r.Year == year) score += 50;
        // Year proximity: off by 1 (alternate releases, director cuts)
        else if (!string.IsNullOrWhiteSpace(year) && !string.IsNullOrWhiteSpace(r.Year)
            && int.TryParse(year, out int qY) && int.TryParse(r.Year, out int rY)
            && Math.Abs(qY - rY) == 1)               score += 10;

        // Popularity tiebreaker: highly-voted entries get a small bonus so
        // "The Mummy (1999)" beats "The Mummy (1932)" when both match equally.
        // Capped at +10 so it can't override a year or title match.
        if (r.Popularity > 0)
            score += (int)Math.Min(10, Math.Log10(r.Popularity + 1) * 3);

        return score;
    }

    private static readonly System.Text.RegularExpressions.Regex _scoreStripRegex =
        new(@"[^\w\s]", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly string[] _scoreArticles = ["the ", "a ", "an "];

    private static string NormaliseForScore(string s)
    {
        // Strip punctuation, collapse spaces
        var clean = _scoreStripRegex.Replace(s.ToLowerInvariant(), " ");
        clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
        // Strip leading articles for comparison ("the dark knight" = "dark knight")
        foreach (var art in _scoreArticles)
            if (clean.StartsWith(art)) { clean = clean[art.Length..]; break; }
        return clean;
    }

    /// <summary>
    /// Selects the next file in the FILES panel that has a write error,
    /// cycling back to the first failure if at the end of the list.
    /// </summary>
    private void JumpToNextFailure()
    {
        var failed = Files
            .Where(f => !f.IsSeparator && f.HasError)
            .ToList();

        if (failed.Count == 0) return;

        // Find the next failure after the currently selected file
        int currentIndex = SelectedFile == null ? -1
            : Files.IndexOf(SelectedFile);

        var next = failed.FirstOrDefault(f => Files.IndexOf(f) > currentIndex)
                   ?? failed[0]; // wrap to first

        SelectedFile = next;
    }

    private void CachePendingChanges()
    {
        if (_selectedFile == null) return;
        CopyMetadataTo(EditingMetadata, _selectedFile.PendingMetadata);
    }

    private void ClearEditing()
    {
        OriginalFileName    = string.Empty;
        MediaInfo           = null;
        IsPreviewPlaying    = false;
        EditingMetadata.Title       = "";
        EditingMetadata.Year        = "";
        EditingMetadata.Description = "";
        EditingMetadata.Genre       = "";
        EditingMetadata.Director    = "";
        EditingMetadata.Cast        = "";
        ArtworkImage    = null;
        FormatWarning   = null;
        RenamePreview   = "";
        ParsedFileInfo  = null;
    }

    private static void CopyMetadataTo(MovieMetadata source, MovieMetadata target)
    {
        target.Title        = source.Title;
        target.Year         = source.Year;
        target.Description  = source.Description;
        target.Genre        = source.Genre;
        target.Director     = source.Director;
        target.Cast         = source.Cast;
        target.ImdbId       = source.ImdbId;
        target.TmdbId       = source.TmdbId;
        target.ArtworkBytes = source.ArtworkBytes;
        target.PosterUrl    = source.PosterUrl;
        target.Rating       = source.Rating;
        target.RatingVotes  = source.RatingVotes;
        target.MpaRating    = source.MpaRating;
        // TV / Episode fields
        target.IsEpisode     = source.IsEpisode;
        target.ShowTitle     = source.ShowTitle;
        target.Season        = source.Season;
        target.Episode       = source.Episode;
        target.EpisodeTitle  = source.EpisodeTitle;
        target.AiredDate     = source.AiredDate;
        target.TvdbId        = source.TvdbId;
        target.TmdbSeriesId  = source.TmdbSeriesId;
    }

    // ── API Search ────────────────────────────────────────────────────────────

    private async Task SearchApiAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;
        var ct = BeginOperation();
        IsBusy = true;
        StatusText = "Searching…";
        SearchResults.Clear();

        try
        {
            // ── ID-based refinement if user has entered an IMDB or TMDB ID ────────
            var imdbId = EditingMetadata.ImdbId?.Trim();
            var tmdbId = EditingMetadata.TmdbId?.Trim();

            MovieMetadata? directMeta = null;

            if (!string.IsNullOrWhiteSpace(tmdbId) && !string.IsNullOrWhiteSpace(Settings.TmdbApiKey))
                { var (_dm, _) = await _apiService.GetTmdbDetailsAsync(tmdbId, TmdbKey); directMeta = _dm; }
            else if (!string.IsNullOrWhiteSpace(imdbId) && !string.IsNullOrWhiteSpace(Settings.TmdbApiKey))
                { var (_dm, _) = await _apiService.GetTmdbByImdbIdAsync(imdbId, TmdbKey); directMeta = _dm; }
            else if (!string.IsNullOrWhiteSpace(imdbId) && !string.IsNullOrWhiteSpace(OmdbKey))
                { var (_dm, _) = await _apiService.GetOmdbDetailsAsync(imdbId, OmdbKey); directMeta = _dm; }

            if (directMeta != null)
            {
                SearchResults.Add(new SearchResult
                {
                    Title     = directMeta.Title,
                    Year      = directMeta.Year,
                    ImdbId    = directMeta.ImdbId,
                    TmdbId    = directMeta.TmdbId,
                    PosterUrl = directMeta.PosterUrl,
                    Overview  = directMeta.Description
                });
                RetrievedMetadata = directMeta;
                if (SelectedFile != null) SelectedFile.RetrievedMetadata = directMeta;
                StatusText = $"Found via ID: {directMeta.Title} ({directMeta.Year})";
                return;
            }

            // ── Regular title search ──────────────────────────────────────────────
            List<SearchResult> tmdbResults;
            List<SearchResult> omdbResults = [];
            string? tmdbSearchErr;
            string? omdbSearchErr = null;

            if (IsEpisodeMode)
            {
                // TV mode: search TMDB /search/tv only
                (tmdbResults, tmdbSearchErr) = await _apiService.SearchTvAsync(SearchQuery, TmdbKey, ct);
                foreach (var r in tmdbResults) r.Source = "TMDB_TV";
            }
            else
            {
                // Movie mode: search movie endpoint + ALSO TV endpoint in parallel
                // This lets users find TV shows even when a file was accidentally renamed
                // to movie format (no S##E## in name, IsEpisode=false in embedded metadata)
                var _searchYear = SelectedFile?.ParsedYear;
                var movieTask = _apiService.SearchTmdbAsync(SearchQuery, TmdbKey, _searchYear, ct);
                var tvTask    = _apiService.SearchTvAsync(SearchQuery, TmdbKey, ct);
                var omdbTask  = _apiService.SearchOmdbAsync(SearchQuery, OmdbKey, ct);

                await Task.WhenAll(movieTask, tvTask, omdbTask);

                var (movieList, movieErr) = movieTask.Result;
                var (tvList,    tvErr)    = tvTask.Result;
                var (omdbList,  omdbErr)  = omdbTask.Result;

                tmdbResults   = movieList?.ToList() ?? [];
                tmdbSearchErr = movieErr;

                // TV results appended after movie results, clearly labelled
                var tvResults = tvList?.ToList() ?? [];
                foreach (var r in tvResults) r.Source = "TMDB_TV";
                tmdbResults.AddRange(tvResults);

                omdbResults   = omdbList?.ToList() ?? [];
                omdbSearchErr = omdbErr;
            }
            // Surface API errors to status bar if both failed
            if (!tmdbResults.Any() && !omdbResults.Any())
            {
                var errs = new List<string>();
                if (tmdbSearchErr != null) errs.Add($"TMDB: {tmdbSearchErr}");
                if (omdbSearchErr != null) errs.Add($"OMDB: {omdbSearchErr}");
                if (errs.Any()) StatusText = string.Join("  ·  ", errs);
            }

            _allSearchResults = tmdbResults.Concat(omdbResults)
                                           .DistinctBy(r => r.Title + r.Year)
                                           .ToList();
            _searchPage = 0;
            foreach (var r in _allSearchResults.Take(PageSize))
                SearchResults.Add(r);

            HasMoreResults = _allSearchResults.Count > PageSize;

            var found = SearchResults.Count;

            // Warn when top results are a poor match for the query
            if (found > 0)
            {
                var qNorm     = SearchQuery.ToLowerInvariant().Trim();
                var topTitle  = (_allSearchResults[0].Title ?? string.Empty).ToLowerInvariant();
                var topYear   = _allSearchResults[0].Year ?? string.Empty;

                // Simple word overlap
                var qWords  = new HashSet<string>(qNorm.Split(' ',
                    StringSplitOptions.RemoveEmptyEntries));
                var tWords  = new HashSet<string>(topTitle.Split(' ',
                    StringSplitOptions.RemoveEmptyEntries));
                int common  = qWords.Intersect(tWords).Count();
                int total   = qWords.Union(tWords).Count();
                double score = total == 0 ? 0 : (double)common / total;

                StatusText = found > 0
                    ? $"{_allSearchResults.Count} result(s) found." +
                      (HasMoreResults ? " Scroll for more." : "") +
                      (score < 0.30
                          ? $"  ⚠ Top result '{_allSearchResults[0].Title}' ({topYear}) is a low-confidence match — " +
                            $"TMDB may not have '{SearchQuery}' under this title. Try the original/alternate title."
                          : string.Empty)
                    : "No results found.";
            }
            else
            {
                StatusText = "No results found.";
            }

            // Auto-switch to Retrieved Data tab when results arrive
            if (found > 0 && Settings.AutoSwitchToResults)
                SelectedTabIndex = 1; // Retrieved Data tab
        }
        catch (Exception ex)
        {
            StatusText = $"Search error: {ex.Message}";
            Log( $"[{DateTime.Now:HH:mm:ss}] Search error: {ex.Message}");
        }
        finally
        {
            EndOperation(resetProgress: false);
        }
    }

    private async Task LookupByIdAsync()
    {
        if (string.IsNullOrWhiteSpace(IdLookup)) return;
        IsBusy = true;
        StatusText = "Looking up ID…";

        try
        {
            MovieMetadata? meta = null;
            var id = IdLookup.Trim();

            if (id.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                { var (_m3, _) = await _apiService.GetTmdbByImdbIdAsync(id, TmdbKey); meta = _m3; }
                if (meta == null) { var (_m2, _) = await _apiService.GetOmdbDetailsAsync(id, OmdbKey); meta = _m2; }
            }
            else if (int.TryParse(id, out _))
            {
                { var (_m, _) = await _apiService.GetTmdbDetailsAsync(id, TmdbKey); meta = _m; }
            }

            if (meta != null)
            {
                RetrievedMetadata = meta;
                if (SelectedFile != null) SelectedFile.RetrievedMetadata = meta;
                StatusText = $"Found: {meta.Title} ({meta.Year})";
            }
            else
            {
                StatusText = "No metadata found for that ID.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Lookup error: {ex.Message}";
        }
        finally
        {
            EndOperation(resetProgress: false);
        }
    }

    // Cancels any in-flight LoadSearchResultDetailsAsync before starting a new one.
    // Prevents race condition where rapid clicks on two results merge episode data.
    public CancellationTokenSource? _searchDetailCts;

    public async Task LoadSearchResultDetailsAsync(SearchResult result)
    {
        if (result == null) return;

        // Cancel and replace any running detail load
        var oldCts = Interlocked.Exchange(ref _searchDetailCts, new CancellationTokenSource());
        oldCts?.Cancel();
        oldCts?.Dispose();
        var detailCt = _searchDetailCts!.Token;

        // ── TV path: show picker immediately — no IsBusy needed ─────────────
        // ShowTvPickerAsync is a UI interaction driver (shows season/episode dropdowns).
        // Running it under IsBusy=true means any file-switch that calls
        // OnSelectedFileChanged (which cancels _tvPickerCts) leaves IsBusy stuck true.
        if ((result.Source == "TMDB_TV" || IsEpisodeMode) && !string.IsNullOrWhiteSpace(result.TmdbId))
        {
            if (detailCt.IsCancellationRequested) return;
            StatusText = $"Loading seasons for {result.Title}…";
            await ShowTvPickerAsync(result.TmdbId, result.Title);
            // ShowTvPickerAsync now awaits episode loading, so episodes are ready here.
            StatusText = TvEpisodeItems.Count > 0
                ? $"✓ {result.Title} — Season {TvSelectedSeason}, {TvEpisodeItems.Count} episode(s) loaded. Select episode then click Load This Episode."
                : $"Season data loaded for {result.Title} — no episodes found for Season {TvSelectedSeason}.";
            return;
        }

        // ── Movie path: use IsBusy as before ──────────────────────────────────
        IsBusy = true;
        StatusText = $"Loading: {result.Title}…";

        try
        {
            MovieMetadata? meta = null;

            {
                // Movie mode — existing flow
                if (!string.IsNullOrWhiteSpace(result.TmdbId))
                    { var (_m, _) = await _apiService.GetTmdbDetailsAsync(result.TmdbId, TmdbKey); meta = _m; }
                if (meta == null && !string.IsNullOrWhiteSpace(result.ImdbId))
                    { var (_m, _) = await _apiService.GetOmdbDetailsAsync(result.ImdbId, OmdbKey); meta = _m; }
            }

            if (meta != null)
            {
                if (string.IsNullOrWhiteSpace(meta.MpaRating)) meta.MpaRating = "NR";
                RetrievedMetadata = meta;
                if (SelectedFile != null) SelectedFile.RetrievedMetadata = meta;
                StatusText = result.Source == "TMDB_TV"
                    ? $"Loaded: {meta.ShowTitle} S{meta.Season:D2}E{meta.Episode:D2} — {meta.EpisodeTitle}"
                    : $"Loaded: {meta.Title} ({meta.Year})";
            }
            else
            {
                StatusText = "Failed to load details.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Load error: {ex.Message}";
        }
        finally
        {
            EndOperation(resetProgress: false);
        }
    }

}
