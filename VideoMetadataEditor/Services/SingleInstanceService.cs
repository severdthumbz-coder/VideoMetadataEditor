using System.Threading;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Stops the SAME build from running twice (build 166 fix).
///
/// Before this, double-clicking the EXE while it was already running — or a
/// "Start with Windows" copy sitting in the tray when you launched it again — started a
/// second process. Both showed a tray icon with the identical label, and both ran
/// folder watchers over the same library, which can race on renames and embeds.
///
/// Scope is deliberately per BUILD, not per app: the mutex name includes the full
/// version, so v1.4.0.165 and v1.4.0.166 can still be open side by side for comparison
/// (the tray label exists precisely to tell them apart). It is also per Windows user
/// (Local\ namespace = per session).
///
/// A second launch signals the first through a named event; the first restores and
/// focuses its window, then the second exits quietly.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _showEvent;
    private readonly bool _owns;
    private RegisteredWaitHandle? _registration;

    private SingleInstanceService(Mutex mutex, EventWaitHandle showEvent, bool owns)
    {
        _mutex = mutex;
        _showEvent = showEvent;
        _owns = owns;
    }

    /// <summary>True when this process is the first copy of this build.</summary>
    public bool IsPrimary => _owns;

    /// <summary>Mutex/event base name for a build, e.g. "VideoMetadataEditor_1.4.0.166".</summary>
    public static string NameFor(string buildVersion) => $"VideoMetadataEditor_{buildVersion}";

    public static SingleInstanceService Acquire(string buildVersion)
    {
        var baseName = @"Local\" + NameFor(buildVersion);
        var mutex = new Mutex(initiallyOwned: true, baseName + "_Mutex", out bool createdNew);
        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, baseName + "_Show");
        return new SingleInstanceService(mutex, evt, createdNew);
    }

    /// <summary>Second copy: ask the running copy to show itself.</summary>
    public void SignalPrimary()
    {
        try { _showEvent.Set(); } catch { /* primary just exited — nothing to wake */ }
    }

    /// <summary>
    /// First copy: invoke <paramref name="onShowRequested"/> (on a pool thread) every
    /// time another launch of this build asks us to come forward.
    /// </summary>
    public void ListenForShowRequests(Action onShowRequested)
    {
        if (!_owns || _registration != null) return;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _showEvent, (_, _) => onShowRequested(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        if (_owns)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { /* not owned on this thread */ }
        }
        _mutex.Dispose();
        _showEvent.Dispose();
    }
}
