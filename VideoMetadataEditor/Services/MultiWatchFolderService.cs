using System.IO;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Wraps multiple <see cref="WatchFolderService"/> instances — one per folder.
/// Exposes the same FileDetected / WatcherError events so the ViewModel has one
/// uniform subscription point regardless of how many folders are watched.
/// </summary>
public sealed class MultiWatchFolderService : IDisposable
{
    private readonly List<WatchFolderService> _watchers = new();

    public event Action<string>? FileDetected;
    public event Action<string>? WatcherError;

    public bool IsActive => _watchers.Any(w => w.IsActive);

    public void Start(IEnumerable<string> folders, IEnumerable<string> existingPaths, int pollMinutes, bool recursive = true)
    {
        Stop();
        var existing = existingPaths.ToList();
        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f) && Directory.Exists(f)))
        {
            var w = new WatchFolderService();
            w.FileDetected += path => FileDetected?.Invoke(path);
            w.WatcherError += msg  => WatcherError?.Invoke(msg);
            w.Start(folder, existing, pollMinutes, recursive);
            _watchers.Add(w);
        }
    }

    public void Stop()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    public void AddKnownPath(string path)
    {
        foreach (var w in _watchers) w.AddKnownPath(path);
    }

    public void ClearKnownPaths()
    {
        foreach (var w in _watchers) w.ClearKnownPaths();
    }

    /// <summary>
    /// Updates the poll interval on all active watchers without stopping them.
    /// Preserves _knownPaths — no files will be re-reported.
    /// </summary>
    public void UpdatePollInterval(int pollMinutes)
    {
        foreach (var w in _watchers) w.UpdatePollInterval(pollMinutes);
    }

    public void Dispose() => Stop();
}
