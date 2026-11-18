using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using VideoMetadataEditor.Services;

namespace VideoMetadataEditor.ViewModels;

/// <summary>
/// Library audio/subtitle tracks (build 175).
///
/// After a scan (and after posters), a background pass reads the tracks inside each file
/// with ffprobe — or ffmpeg when ffprobe isn't there — three files at a time. Results go
/// onto the entry and into the scan cache, so only new or changed files are read again.
/// The Library gets two columns (Audio, Subs in file) and two filters (Audio, Subtitles)
/// that work alongside the tab, the text filter and the Show filter.
/// </summary>
public partial class MainViewModel
{
    public ObservableCollection<TrackSummary.FilterOption> LibraryAudioOptions { get; } =
        new() { new(TrackSummary.Any, "Any audio") };

    public ObservableCollection<TrackSummary.FilterOption> LibrarySubtitleOptions { get; } =
        new() { new(TrackSummary.Any, "Any subtitles") };

    private string _libraryAudioFilter = TrackSummary.Any;
    /// <summary>Audio filter key: "any", "multi" or "lang:xx".</summary>
    public string LibraryAudioFilter
    {
        get => _libraryAudioFilter;
        set
        {
            // null comes from the ComboBox while its list is being rebuilt — ignore it.
            if (value == null || value == _libraryAudioFilter) return;
            Set(ref _libraryAudioFilter, value);
            OnTrackFilterChanged();
        }
    }

    private string _librarySubtitleFilter = TrackSummary.Any;
    /// <summary>Subtitle filter key: "any", "none", "lang:xx" or "missing:xx".</summary>
    public string LibrarySubtitleFilter
    {
        get => _librarySubtitleFilter;
        set
        {
            if (value == null || value == _librarySubtitleFilter) return;
            Set(ref _librarySubtitleFilter, value);
            OnTrackFilterChanged();
        }
    }

    public bool IsTrackFilterActive =>
        _libraryAudioFilter != TrackSummary.Any || _librarySubtitleFilter != TrackSummary.Any;

    private void OnTrackFilterChanged()
    {
        LibraryView.Refresh();
        RaiseProperty(nameof(TvShowTree));
        RaiseProperty(nameof(IsTrackFilterActive));
        RaiseProperty(nameof(IsAttentionFilterActive));
        RaiseLibraryAttentionCounts();
        LogService.Debug("Library", $"Track filter → audio: {_libraryAudioFilter}, subtitles: {_librarySubtitleFilter}");
    }

    private bool MatchesTrackFilters(Models.LibraryEntry e) =>
        TrackSummary.MatchesAudio(_libraryAudioFilter, e.Tracks) &&
        TrackSummary.MatchesSubtitles(_librarySubtitleFilter, e.Tracks, e.SidecarSubtitleLanguages, e.Subtitles.Count);

    // ── Progress ──────────────────────────────────────────────────────────────

    private int _tracksPending;
    /// <summary>Files whose tracks are still being read.</summary>
    public int TracksPendingCount => Volatile.Read(ref _tracksPending);

    private bool _trackToolMissing;
    /// <summary>True when neither ffprobe nor ffmpeg is available, so tracks can't be read.</summary>
    public bool TrackToolMissing => _trackToolMissing;

    // ── Filter choices ────────────────────────────────────────────────────────

    /// <summary>Rebuilds the Audio and Subtitles choices from the languages in the library (UI thread).</summary>
    private void RebuildTrackFilterOptions()
    {
        var entries = LibraryEntries.ToList();
        ReplaceOptions(LibraryAudioOptions,
            TrackSummary.BuildAudioOptions(entries.Select(e => e.Tracks)), _libraryAudioFilter);
        ReplaceOptions(LibrarySubtitleOptions,
            TrackSummary.BuildSubtitleOptions(entries.Select(e =>
                (e.Tracks, e.SidecarSubtitleLanguages, e.Subtitles.Count))), _librarySubtitleFilter);
        RaiseProperty(nameof(LibraryAudioFilter));
        RaiseProperty(nameof(LibrarySubtitleFilter));
    }

    private static void ReplaceOptions(ObservableCollection<TrackSummary.FilterOption> target,
                                       List<TrackSummary.FilterOption> fresh, string selectedKey)
    {
        // Keep the current choice available even if no file matches it any more.
        if (!fresh.Any(o => o.Key == selectedKey))
            fresh.Add(new(selectedKey, LabelForMissingKey(selectedKey)));
        if (target.Select(o => o.Key + o.Label).SequenceEqual(fresh.Select(o => o.Key + o.Label))) return;
        target.Clear();
        foreach (var o in fresh) target.Add(o);
    }

    private static string LabelForMissingKey(string key) =>
        key.StartsWith("lang:", StringComparison.Ordinal)    ? $"{TrackSummary.LanguageName(key[5..])} (0)"
      : key.StartsWith("missing:", StringComparison.Ordinal) ? $"No {TrackSummary.LanguageName(key[8..])} subtitles"
      : key == "multi" ? "🔊 2+ audio tracks (0)"
      : key == "none"  ? "💬 No subtitles at all (0)"
      : key;

    // ── Background pass ───────────────────────────────────────────────────────

    /// <summary>
    /// Reads the tracks of every entry that doesn't have them yet. Files on screen in the
    /// active tab go first. Safe to call with entries that are already done (they're skipped).
    /// </summary>
    private async Task LoadLibraryTracksAsync(IReadOnlyList<Models.LibraryEntry> entries,
                                              string? activeTabPath, CancellationToken ct)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        var todo = entries
            .Where(e => e.Tracks == null)
            .OrderBy(e => !string.IsNullOrEmpty(activeTabPath) &&
                          e.FilePath.StartsWith(activeTabPath, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();

        if (todo.Count == 0)
        {
            await dispatcher.InvokeAsync(RebuildTrackFilterOptions);
            return;
        }

        var ffprobe = await Task.Run(() => MediaProbeService.FindFfprobe());
        var ffmpeg  = ffprobe == null ? await Task.Run(() => FfmpegService.Detect()) : null;
        if (ffprobe == null && ffmpeg == null)
        {
            if (!_trackToolMissing)
                LogService.Warn("Library",
                    "Audio and subtitle track info needs FFmpeg — install it from Settings → External Tools, then scan the library again.");
            _trackToolMissing = true;
            await dispatcher.InvokeAsync(() =>
            {
                RaiseProperty(nameof(TrackToolMissing));
                RaiseProperty(nameof(LibraryBackgroundStatus));
                RebuildTrackFilterOptions();
                RaiseLibraryAttentionCounts();
            });
            return;
        }
        if (_trackToolMissing)
        {
            _trackToolMissing = false;
            await dispatcher.InvokeAsync(() => RaiseProperty(nameof(TrackToolMissing)));
        }

        Interlocked.Add(ref _tracksPending, todo.Count);
        RaiseTrackProgress();

        // Build 176: as many ffprobe processes as the drive handles well (HDD 2, SSD up to
        // 8) instead of a fixed 3. Each probe only reads the file's header.
        int workers;
        try { workers = Math.Clamp(DriveCapabilityService.GetProfile(todo[0].FilePath).RecommendedMetadataWorkers, 2, 8); }
        catch { workers = 3; }

        // One file (Library Watch) gets a single result line, not an operation.
        bool single = todo.Count == 1;
        using var op = single ? null : LogService.Begin("Library", $"Reading audio/subtitle tracks for {todo.Count:N0} file(s)",
            (ffprobe != null ? $"Using {ffprobe}" : $"Using {ffmpeg} (ffprobe not found — slower)") +
            $"{Environment.NewLine}{workers} at a time");

        int ok = 0, failed = 0, remaining = todo.Count, done = 0;
        int total = todo.Count;
        int step  = Math.Max(250, total / 10);   // progress line every ~10 %
        var failures = new ConcurrentQueue<string>();
        var pending  = new List<(Models.LibraryEntry Entry, string Tracks)>();
        var gate     = new object();

        async Task Flush()
        {
            List<(Models.LibraryEntry Entry, string Tracks)> batch;
            lock (gate) { batch = pending.ToList(); pending.Clear(); }
            if (batch.Count == 0) return;
            await dispatcher.InvokeAsync(() =>
            {
                foreach (var (entry, tracks) in batch) entry.Tracks = tracks;
                if (IsTrackFilterActive) LibraryView.Refresh();
                RaiseLibraryAttentionCounts();
                RaiseTrackProgress();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        try
        {
            await Task.Run(async () =>
            {
                using var sem = new SemaphoreSlim(workers);
                var tasks = todo.Select(async e =>
                {
                    await sem.WaitAsync(ct);
                    try
                    {
                        if (!File.Exists(e.FilePath))
                        {
                            Interlocked.Increment(ref failed);
                            failures.Enqueue($"{e.FilePath} — no longer there");
                            return;
                        }
                        var probe = await MediaProbeService.ProbeAsync(e.FilePath, ct);
                        if (probe == null)
                        {
                            Interlocked.Increment(ref failed);
                            failures.Enqueue($"{e.FilePath} — couldn't be read");
                            return;
                        }
                        var enc = TrackSummary.Encode(probe);
                        _libraryCacheService.UpdateTracks(e.FilePath, enc);
                        lock (gate) pending.Add((e, enc));
                        Interlocked.Increment(ref ok);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref failed);
                        failures.Enqueue($"{e.FilePath} — {ex.Message}");
                    }
                    finally
                    {
                        sem.Release();
                        Interlocked.Decrement(ref _tracksPending);
                        Interlocked.Decrement(ref remaining);
                        var n = Interlocked.Increment(ref done);
                        // Show results in steady batches while the pass runs.
                        if (n % 20 == 0) await Flush();
                        if (!single && n % step == 0 && n < total)
                        {
                            LogService.Debug("Library", $"Tracks: {n:N0} of {total:N0} read");
                            // Save as we go, so a long first pass isn't lost if VME is closed.
                            if (n % (step * 2) == 0) await SaveLibraryCacheAsync();
                        }
                    }
                }).ToList();
                await Task.WhenAll(tasks);
            }, ct);
            await Flush();

            if (single)
            {
                var e = todo[0];
                if (failed > 0)
                    LogService.Warn("Library", $"Tracks couldn't be read: {e.FileName}",
                        failures.FirstOrDefault(), e.FilePath);
                else
                {
                    var audio = TrackSummary.AudioDisplay(e.Tracks);
                    var subs  = TrackSummary.SubtitleDisplay(e.Tracks);
                    LogService.Debug("Library", $"Tracks: {e.FileName} — audio {audio}, subs {subs}", null, e.FilePath);
                }
            }
            else
            {
                op!.Outcome = failed == 0 ? $"{ok:N0} file(s) read"
                                          : $"{ok:N0} file(s) read, {failed} couldn't be read";
                if (failed > 0)
                {
                    op.Warn();
                    LogService.Warn("Library", $"Tracks couldn't be read for {failed} file(s)",
                        string.Join(Environment.NewLine, failures.Take(50)) +
                        (failed > 50 ? $"{Environment.NewLine}…and {failed - 50} more" : string.Empty));
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (op != null)
            {
                op.Cancel();
                op.Outcome = $"{ok:N0} read before the scan was restarted or cancelled";
            }
            await Flush();
        }
        finally
        {
            // Anything not processed (cancelled) no longer counts as pending.
            var left = Volatile.Read(ref remaining);
            if (left > 0) Interlocked.Add(ref _tracksPending, -left);
        }

        await dispatcher.InvokeAsync(() =>
        {
            RebuildTrackFilterOptions();
            if (IsTrackFilterActive) { LibraryView.Refresh(); RaiseProperty(nameof(TvShowTree)); }
            RaiseLibraryAttentionCounts();
            RaiseTrackProgress();
        });
        // Persist the new track info so the next start doesn't read these files again.
        await SaveLibraryCacheAsync();
    }

    /// <summary>Saves the library cache for every file currently in the Library.</summary>
    private async Task SaveLibraryCacheAsync()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        var livePaths = await dispatcher.InvokeAsync(() => LibraryEntries.Select(x => x.FilePath).ToList());
        if (livePaths.Count > 0) await _libraryCacheService.SaveAsync(livePaths);
    }

    /// <summary>
    /// Build 176: visible progress for the background passes, shown under the Library
    /// filters — e.g. "Reading audio/subtitle tracks… 1,240 of 4,394 left".
    /// </summary>
    public string LibraryBackgroundStatus
    {
        get
        {
            var tracks = TracksPendingCount;
            if (tracks > 0) return $"🔊 Reading audio/subtitle tracks… {tracks:N0} left";
            if (TrackToolMissing) return "🔊 Track info needs FFmpeg — Settings → External Tools";
            return string.Empty;
        }
    }

    private void RaiseTrackProgress()
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d == null) return;
        if (d.CheckAccess()) RaiseProperty(nameof(LibraryBackgroundStatus));
        else d.InvokeAsync(() => RaiseProperty(nameof(LibraryBackgroundStatus)));
    }
}
