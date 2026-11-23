using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;

namespace VideoMetadataEditor.ViewModels;

/// <summary>
/// One watch-folder file that auto-embed didn't embed on its own (build 178): the match
/// was ambiguous (e.g. two TMDB entries with the same title and year), not confident
/// enough, or you've chosen to pick movie matches yourself.
/// </summary>
public sealed class ReviewItem : INotifyPropertyChanged
{
    public required VideoFile File { get; init; }
    public required bool   IsEpisode { get; init; }
    public int?   Season   { get; init; }
    public int?   Episode  { get; init; }
    public required string Query  { get; init; }
    public string Year   { get; init; } = string.Empty;
    public required string Reason { get; init; }
    public required IReadOnlyList<SearchResult> Candidates { get; init; }
    public DateTime Added { get; } = DateTime.Now;

    private SearchResult? _selected;
    public SearchResult? Selected
    {
        get => _selected;
        set { _selected = value; OnChanged(); }
    }

    private bool _busy;
    public bool IsBusy { get => _busy; set { _busy = value; OnChanged(); } }

    public string FileName => File.FileName;
    public string What => IsEpisode && Season.HasValue && Episode.HasValue
        ? $"📺 {Query}  S{Season:D2}E{Episode:D2}"
        : $"🎬 {Query}{(string.IsNullOrWhiteSpace(Year) ? "" : $" ({Year})")}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
}

public partial class MainViewModel
{
    /// <summary>Watch-folder files waiting for you to confirm their match (build 178).</summary>
    public ObservableCollection<ReviewItem> ReviewQueue { get; } = new();

    public int  ReviewQueueCount => ReviewQueue.Count;
    public bool HasReviewItems   => ReviewQueue.Count > 0;

    /// <summary>Settings → Watch folder: send every movie to the review queue instead of auto-embedding.</summary>
    public bool AutoEmbedReviewMovies
    {
        get => Settings.AutoEmbedReviewMovies;
        set { Settings.AutoEmbedReviewMovies = value; RaiseProperty(); _ = App.ConfigService.SaveAsync(); }
    }

    private void RaiseReviewCounts()
    {
        RaiseProperty(nameof(ReviewQueueCount));
        RaiseProperty(nameof(HasReviewItems));
    }

    /// <summary>Adds (or refreshes) a file's entry in the review queue. UI thread.</summary>
    private void QueueForReview(VideoFile vf, AutoMatch.Decision d, string query, string? year,
                                bool isEpisode, int? season = null, int? episode = null)
    {
        var existing = ReviewQueue.FirstOrDefault(r => ReferenceEquals(r.File, vf));
        if (existing != null) ReviewQueue.Remove(existing);

        var item = new ReviewItem
        {
            File = vf, IsEpisode = isEpisode, Season = season, Episode = episode,
            Query = query, Year = year ?? string.Empty, Reason = d.Reason, Candidates = d.Candidates,
        };
        item.Selected = d.Best != null && d.Candidates.Contains(d.Best) ? d.Best : d.Candidates.FirstOrDefault();
        ReviewQueue.Add(item);
        vf.IsNewFile = true;   // stays highlighted in the Files panel until it's handled
        RaiseReviewCounts();

        LogService.Info(isEpisode ? "TV" : "Embed",
            $"🗂 Review queue: {vf.FileName} — {d.Reason}",
            d.Candidates.Count > 0 ? "Candidates:" + Environment.NewLine + AutoMatch.DescribeCandidates(d.Candidates) : null,
            vf.FilePath);
        StatusText = $"🗂 {vf.FileName} needs your choice — {ReviewQueue.Count} waiting in the review queue.";
    }

    private ICommand? _openReviewQueueCommand;
    /// <summary>Opens the review queue window (toolbar "🗂 Review (n)").</summary>
    public ICommand OpenReviewQueueCommand => _openReviewQueueCommand ??= new RelayCommand(_ =>
        Views.ReviewQueueWindow.Open(this));

    /// <summary>Embeds <paramref name="choice"/> into the item's file and removes it from the queue.</summary>
    internal async Task<bool> ApproveReviewAsync(ReviewItem item, SearchResult choice)
    {
        if (item.IsBusy) return false;
        if (!File.Exists(item.File.FilePath))
        {
            LogService.Warn("Embed", $"Review queue: {item.FileName} is no longer there — removed", null, item.File.FilePath);
            ReviewQueue.Remove(item); RaiseReviewCounts();
            return false;
        }

        item.IsBusy = true;
        using var op = LogService.Begin(item.IsEpisode ? "TV" : "Embed",
            $"Review queue: embed \"{choice.Title}\" ({choice.Year}) into {item.FileName}");
        try
        {
            MovieMetadata? meta;
            string? err;
            if (item.IsEpisode && item.Season.HasValue && item.Episode.HasValue)
            {
                (meta, err) = await _apiService.GetTvEpisodeAsync(choice.TmdbId, item.Season.Value, item.Episode.Value, TmdbKey);
                if (meta != null) { meta.IsEpisode = true; meta.TmdbSeriesId = choice.TmdbId; }
            }
            else
            {
                (meta, err) = await _apiService.GetTmdbDetailsAsync(choice.TmdbId, TmdbKey);
                if (meta != null && string.IsNullOrWhiteSpace(meta.MpaRating)) meta.MpaRating = "NR";
            }

            if (meta == null)
            {
                op.Fail(err ?? "no details returned");
                StatusText = $"Couldn't load details for \"{choice.Title}\": {err}";
                return false;
            }

            await EmbedTvEpisodeAsync(item.File, meta, CancellationToken.None);
            if (item.File.WriteStatus == WriteStatus.Failed)
            {
                op.Fail("the write failed — see the lines above");
                return false;
            }

            item.File.IsNewFile = false;
            ReviewQueue.Remove(item);
            RaiseReviewCounts();
            op.Outcome = $"embedded as \"{meta.Title}\"";
            StatusText = $"✓ Embedded {item.File.FileName} as \"{meta.Title}\" — {ReviewQueue.Count} left to review.";
            return true;
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            StatusText = $"Embed failed: {ex.Message}";
            return false;
        }
        finally { item.IsBusy = false; }
    }

    /// <summary>Removes a file from the queue without embedding (it stays in the Files panel).</summary>
    internal void SkipReview(ReviewItem item)
    {
        ReviewQueue.Remove(item);
        RaiseReviewCounts();
        LogService.Info(item.IsEpisode ? "TV" : "Embed", $"Review queue: skipped {item.FileName} — left for you", null, item.File.FilePath);
    }

    /// <summary>Selects the file in the Files panel and opens Retrieved Data to search by hand.</summary>
    internal void SearchReviewManually(ReviewItem item)
    {
        SelectedFile = item.File;
        SelectedTabIndex = 1;
        ReviewQueue.Remove(item);
        RaiseReviewCounts();
    }

    /// <summary>Drops queue entries whose file has left the Files panel (moved, removed, cleared).</summary>
    private void PruneReviewQueue()
    {
        var gone = ReviewQueue.Where(r => !Files.Contains(r.File)).ToList();
        foreach (var r in gone) ReviewQueue.Remove(r);
        if (gone.Count > 0) RaiseReviewCounts();
    }
}
