using System;
using System.Threading.Tasks;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Safe fire-and-forget execution. WPF code frequently kicks off background work with
/// "_ = Task.Run(...)" — but if that task throws, the exception is unobserved and gets
/// rethrown by the finalizer thread later (the UnobservedTaskException class of bug).
///
/// Run() and RunDelayed() guarantee the work is wrapped in try/catch so an exception is
/// logged and swallowed at the point of origin rather than surfacing unpredictably later.
///
/// Use these instead of bare "_ = Task.Run(...)" for any fire-and-forget work whose result
/// nobody awaits.
/// </summary>
public static class BackgroundTask
{
    /// <summary>Optional sink for logging caught exceptions (wired by App at startup).</summary>
    public static Action<string, Exception>? OnError { get; set; }

    /// <summary>Runs an async fire-and-forget action with exception isolation.</summary>
    public static void Run(Func<Task> work, string context = "BackgroundTask")
    {
        _ = Task.Run(async () =>
        {
            try { await work().ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected on cancel — ignore */ }
            catch (Exception ex) { Report(context, ex); }
        });
    }

    /// <summary>Runs an async fire-and-forget action that observes a cancellation token.</summary>
    public static void Run(Func<System.Threading.CancellationToken, Task> work,
        System.Threading.CancellationToken ct, string context = "BackgroundTask")
    {
        _ = Task.Run(async () =>
        {
            try { await work(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Report(context, ex); }
        }, ct);
    }

    /// <summary>Runs a synchronous fire-and-forget action with exception isolation.</summary>
    public static void Run(Action work, string context = "BackgroundTask")
    {
        _ = Task.Run(() =>
        {
            try { work(); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Report(context, ex); }
        });
    }

    /// <summary>Runs async work after a delay, with exception isolation.</summary>
    public static void RunDelayed(int delayMs, Func<Task> work, string context = "BackgroundTask")
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Report(context, ex); }
        });
    }

    private static void Report(string context, Exception ex)
    {
        try { OnError?.Invoke(context, ex); }
        catch { /* never let the error sink itself throw */ }
        System.Diagnostics.Debug.WriteLine($"[{context}] {ex.GetType().Name}: {ex.Message}");
    }
}
