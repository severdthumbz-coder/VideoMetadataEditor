using System.IO;
using System.Timers;
using SystemTimer = System.Timers.Timer;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Monitors a folder for new video files using both FileSystemWatcher (instant)
/// and a System.Timers.Timer poll (fallback for network paths and missed events).
///
/// Key design decisions:
///   - System.Timers.Timer runs on a thread-pool thread — immune to UI thread
///     stalls, always fires at the configured interval even if the UI is busy.
///   - FSW auto-restarts on error with exponential back-off up to 5 retries,
///     followed by an immediate poll scan to catch anything missed during the gap.
///   - _knownPaths is pruned every 10 polls to prevent indefinite growth.
///   - Calling Start() preserves existing _knownPaths entries so files already
///     loaded are never re-reported after a settings change.
///   - UpdatePollInterval() changes the interval without a full Stop/Start cycle.
///   - On the first poll after Start(), the folder is scanned and all existing
///     files are seeded into _knownPaths so they are NOT reported as new.
///     This prevents the "all files re-reported on launch" problem.
/// </summary>
public sealed class WatchFolderService : IDisposable
{
    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".wmv", ".m4v", ".webm" };

    private FileSystemWatcher? _watcher;
    private SystemTimer?       _pollTimer;
    private string             _folder = string.Empty;
    private int                _fswRestartCount;
    private const int          MaxFswRestarts = 5;
    private bool               _initialSeedDone;   // true once the startup seed pass has run

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _knownPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private int _pruneCounter;

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action<string>? FileDetected;
    public event Action<string>? WatcherError;

    public bool   IsActive        { get; private set; }
    public string MonitoredFolder => _folder;

    // ── Public API ────────────────────────────────────────────────────────────

    private bool _recursive = true;

    public void Start(string folder, IEnumerable<string> existingPaths, int pollMinutes, bool recursive = true)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        _folder          = folder;
        _recursive       = recursive;
        _initialSeedDone = false;
        foreach (var p in existingPaths) _knownPaths.TryAdd(p, 0);

        StartFsw(folder);
        StartPollTimer(pollMinutes);
        IsActive = true;

        // Perform an immediate silent seed pass in the background: enumerate all
        // existing files and add them to _knownPaths WITHOUT raising FileDetected.
        // This prevents the first poll from re-reporting every file in the folder
        // as "new", which is the main cause of the "files reported on first launch" bug.
        _ = Task.Run(() =>
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(
                    folder, "*.*",
                    recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                    .Where(IsVideoFile))
                {
                    _knownPaths.TryAdd(path, 0);  // seed silently — no FileDetected
                }
            }
            catch { }
            finally { _initialSeedDone = true; }
        });
    }

    public void Stop()
    {
        _watcher?.Dispose(); _watcher = null;
        _pollTimer?.Stop();
        _pollTimer?.Dispose(); _pollTimer = null;
        IsActive = false;
        _initialSeedDone = false;
    }

    /// <summary>
    /// Updates the poll interval without restarting the watcher or clearing
    /// known paths. Call when the user changes the interval in Settings.
    /// </summary>
    public void UpdatePollInterval(int pollMinutes)
    {
        if (_pollTimer == null) return;
        _pollTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, pollMinutes)).TotalMilliseconds;
        _pollTimer.Stop();
        _pollTimer.Start();
    }

    public void AddKnownPath(string path)  => _knownPaths.TryAdd(path, 0);
    public void ClearKnownPaths()          => _knownPaths.Clear();

    // ── FSW ───────────────────────────────────────────────────────────────────

    private void StartFsw(string folder)
    {
        try
        {
            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = _recursive,
                NotifyFilter          = NotifyFilters.FileName | NotifyFilters.CreationTime,
                InternalBufferSize    = 65536,
                EnableRaisingEvents   = true
            };
            _watcher.Created += (_, e)  =>
            {
                if (IsVideoFile(e.FullPath) && _initialSeedDone)
                    DispatchDetect(e.FullPath);
            };
            _watcher.Renamed += (_, e)  =>
            {
                if (IsVideoFile(e.FullPath) && _initialSeedDone)
                    DispatchDetect(e.FullPath);
            };
            _watcher.Error   += OnWatcherError;
            _fswRestartCount = 0;
        }
        catch (Exception ex)
        {
            WatcherError?.Invoke($"FSW could not start on '{folder}': {ex.Message}");
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var msg = e.GetException()?.Message ?? "Unknown FSW error";
        WatcherError?.Invoke($"Watch folder FSW error: {msg}");

        if (_fswRestartCount >= MaxFswRestarts)
        {
            WatcherError?.Invoke("FSW restart limit reached — poll timer will continue.");
            return;
        }

        // Exponential back-off restart: 1s, 2s, 4s, 8s, 16s
        var delay = TimeSpan.FromSeconds(Math.Pow(2, _fswRestartCount));
        _fswRestartCount++;

        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            if (!IsActive) return;
            try
            {
                _watcher?.Dispose();
                StartFsw(_folder);
                // After restart, run an immediate poll scan to catch any files
                // that arrived during the FSW gap — otherwise they'd wait up to
                // pollMinutes before being detected.
                OnPollElapsed(null, null!);
            }
            catch { /* second failure — poll timer covers detection */ }
        });
    }

    // ── Poll timer ────────────────────────────────────────────────────────────

    private void StartPollTimer(int pollMinutes)
    {
        _pollTimer = new SystemTimer(TimeSpan.FromMinutes(Math.Max(1, pollMinutes)).TotalMilliseconds)
        {
            AutoReset = true
        };
        _pollTimer.Elapsed += OnPollElapsed;
        _pollTimer.Start();
    }

    private void OnPollElapsed(object? sender, ElapsedEventArgs e)
    {
        // Don't fire until the initial seed pass has finished — otherwise every
        // existing file would be reported as new on the very first poll tick.
        if (!_initialSeedDone) return;

        var folder = _folder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.*",
                _recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(IsVideoFile))
            {
                DispatchDetect(path);
            }
        }
        catch { }

        // Prune stale entries every 10 polls
        if (_pruneCounter++ % 10 == 0)
        {
            var stale = _knownPaths.Keys
                .Where(p => !File.Exists(p) && !p.Contains(".vme_"))
                .Take(500).ToList();
            foreach (var k in stale) _knownPaths.TryRemove(k, out _);
        }
    }

    // ── Dispatch ──────────────────────────────────────────────────────────────

    private void DispatchDetect(string path)
    {
        if (!_knownPaths.TryAdd(path, 0)) return; // already known

        System.Windows.Application.Current?.Dispatcher.InvokeAsync(
            () => FileDetected?.Invoke(path),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private static bool IsVideoFile(string path) =>
        !Path.GetFileName(path).StartsWith(".vme_", StringComparison.OrdinalIgnoreCase)
        && VideoExtensions.Contains(Path.GetExtension(path));

    public void Dispose() => Stop();
}

namespace VideoMetadataEditor.Services;

/// <summary>
/// Monitors a folder for new video files using both FileSystemWatcher (instant)
/// and a System.Timers.Timer poll (fallback for network paths and missed events).
///
/// Key design decisions:
///   - System.Timers.Timer runs on a thread-pool thread — immune to UI thread
///     stalls, always fires at the configured interval even if the UI is busy.
///   - FSW auto-restarts on error with exponential back-off up to 3 retries.
///   - _knownPaths is pruned every 10 polls to prevent indefinite growth.
///   - Calling Start() preserves existing _knownPaths entries so files already
///     loaded are never re-reported after a settings change.
///   - UpdatePollInterval() changes the interval without a full Stop/Start cycle.
/// </summary>
public sealed class WatchFolderService : IDisposable
{
    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".wmv", ".m4v", ".webm" };

    private FileSystemWatcher? _watcher;
    private SystemTimer?       _pollTimer;  // System.Timers.Timer — thread-pool, not UI-thread
    private string             _folder = string.Empty;
    private int                _fswRestartCount;
    private const int          MaxFswRestarts = 5;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _knownPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private int _pruneCounter;

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action<string>? FileDetected;
    public event Action<string>? WatcherError;

    public bool   IsActive        { get; private set; }
    public string MonitoredFolder => _folder;

    // ── Public API ────────────────────────────────────────────────────────────

    private bool _recursive = true;

    public void Start(string folder, IEnumerable<string> existingPaths, int pollMinutes, bool recursive = true)
    {
        Stop();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        _folder    = folder;
        _recursive = recursive;
        foreach (var p in existingPaths) _knownPaths.TryAdd(p, 0);

        StartFsw(folder);
        StartPollTimer(pollMinutes);
        IsActive = true;
    }

    public void Stop()
    {
        _watcher?.Dispose(); _watcher = null;
        _pollTimer?.Stop();
        _pollTimer?.Dispose(); _pollTimer = null;
        IsActive = false;
    }

    /// <summary>
    /// Updates the poll interval without restarting the watcher or clearing
    /// known paths. Call when the user changes the interval in Settings.
    /// </summary>
    public void UpdatePollInterval(int pollMinutes)
    {
        if (_pollTimer == null) return;
        _pollTimer.Interval = TimeSpan.FromMinutes(Math.Max(1, pollMinutes)).TotalMilliseconds;
        // Reset the timer so the new interval takes effect from now
        _pollTimer.Stop();
        _pollTimer.Start();
    }

    public void AddKnownPath(string path)  => _knownPaths.TryAdd(path, 0);
    public void ClearKnownPaths()          => _knownPaths.Clear();

    // ── FSW ───────────────────────────────────────────────────────────────────

    private void StartFsw(string folder)
    {
        try
        {
            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = _recursive,
                NotifyFilter          = NotifyFilters.FileName | NotifyFilters.CreationTime,
                InternalBufferSize    = 65536,
                EnableRaisingEvents   = true
            };
            _watcher.Created += (_, e)  => { if (IsVideoFile(e.FullPath)) DispatchDetect(e.FullPath); };
            _watcher.Renamed += (_, e)  => { if (IsVideoFile(e.FullPath)) DispatchDetect(e.FullPath); };
            _watcher.Error   += OnWatcherError;
            _fswRestartCount = 0;
        }
        catch (Exception ex)
        {
            WatcherError?.Invoke($"FSW could not start on '{folder}': {ex.Message}");
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        var msg = e.GetException()?.Message ?? "Unknown FSW error";
        WatcherError?.Invoke($"Watch folder FSW error: {msg}");

        if (_fswRestartCount >= MaxFswRestarts)
        {
            WatcherError?.Invoke("FSW restart limit reached — poll timer will continue.");
            return;
        }

        // Exponential back-off restart: 1s, 2s, 4s, 8s, 16s
        var delay = TimeSpan.FromSeconds(Math.Pow(2, _fswRestartCount));
        _fswRestartCount++;

        _ = Task.Delay(delay).ContinueWith(_ =>
        {
            if (!IsActive) return;
            try
            {
                _watcher?.Dispose();
                StartFsw(_folder);
            }
            catch { /* second failure — poll timer covers detection */ }
        });
    }

    // ── Poll timer ────────────────────────────────────────────────────────────

    private void StartPollTimer(int pollMinutes)
    {
        _pollTimer = new SystemTimer(TimeSpan.FromMinutes(Math.Max(1, pollMinutes)).TotalMilliseconds)
        {
            AutoReset = true
        };
        _pollTimer.Elapsed += OnPollElapsed;
        _pollTimer.Start();
    }

    private void OnPollElapsed(object? sender, ElapsedEventArgs e)
    {
        // Runs on a thread-pool thread — safe to do I/O here directly
        var folder = _folder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        try
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.*", _recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                                          .Where(IsVideoFile))
            {
                DispatchDetect(path);
            }
        }
        catch { /* folder removed or access denied — timer will retry next interval */ }

        // Prune stale entries every 10 polls
        if (_pruneCounter++ % 10 == 0)
        {
            var stale = _knownPaths.Keys
                .Where(p => !File.Exists(p) && !p.Contains(".vme_"))
                .Take(500).ToList();
            foreach (var k in stale) _knownPaths.TryRemove(k, out _);
        }
    }

    // ── Dispatch ──────────────────────────────────────────────────────────────

    private void DispatchDetect(string path)
    {
        if (!_knownPaths.TryAdd(path, 0)) return; // already known

        // Marshal to UI thread — FileDetected subscribers update ObservableCollections
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(
            () => FileDetected?.Invoke(path),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private static bool IsVideoFile(string path) =>
        !Path.GetFileName(path).StartsWith(".vme_", StringComparison.OrdinalIgnoreCase)
        && VideoExtensions.Contains(Path.GetExtension(path));

    public void Dispose() => Stop();
}
