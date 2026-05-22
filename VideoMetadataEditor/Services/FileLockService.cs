using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace VideoMetadataEditor.Services;

// ── Diagnosis result ──────────────────────────────────────────────────────────

public enum WritabilityIssue
{
    None,
    ReadOnlyAttribute,
    LockedByThisApp,
    LockedByProcess,
    InsufficientPermissions,
    NetworkOrVolume,
    Unknown
}

public record WritabilityReport(
    bool IsWritable,
    WritabilityIssue Issue,
    string Message,
    IReadOnlyList<ProcessInfo> LockingProcesses);

public record ProcessInfo(int Pid, string Name, string Description);

// ── Service ───────────────────────────────────────────────────────────────────

public static class FileLockService
{
    // ── Main diagnosis entry point ────────────────────────────────────────────

    /// <summary>
    /// Performs a full diagnosis of why a file cannot be written to.
    /// Checks in order: read-only attribute → process locks (RestartManager) → NTFS permissions.
    /// </summary>
    public static async Task<WritabilityReport> DiagnoseAsync(string filePath)
    {
        // 1. Read-only attribute
        try
        {
            var info = new FileInfo(filePath);
            if (info.IsReadOnly)
                return new WritabilityReport(false, WritabilityIssue.ReadOnlyAttribute,
                    "File has the read-only attribute set. Use the lock button to clear it.", []);
        }
        catch { }

        // 2. Try a harmless open — fastest way to detect a lock
        bool canOpen = await Task.Run(() => TryOpenForWrite(filePath));
        if (canOpen) return new WritabilityReport(true, WritabilityIssue.None, "File is writable.", []);

        // 3. Check which processes have the file open (Windows Restart Manager)
        var lockers = await Task.Run(() => GetLockingProcesses(filePath));
        if (lockers.Count > 0)
        {
            bool selfLocked = lockers.Any(p => p.Pid == Environment.ProcessId);
            var issue = selfLocked ? WritabilityIssue.LockedByThisApp : WritabilityIssue.LockedByProcess;
            var names  = string.Join(", ", lockers.Select(p => $"{p.Name} (PID {p.Pid})"));
            var msg = selfLocked
                ? "The file is held open by this application's media preview player. Stop the preview before embedding."
                : $"Locked by: {names}. Close those applications and retry.";
            return new WritabilityReport(false, issue, msg, lockers);
        }

        // 4. NTFS permissions check
        try
        {
            new FileInfo(filePath).GetAccessControl();
        }
        catch (UnauthorizedAccessException)
        {
            return new WritabilityReport(false, WritabilityIssue.InsufficientPermissions,
                "Access denied by NTFS permissions. Run the app as Administrator or check file ownership.", []);
        }
        catch { }

        try
        {
            var root = Path.GetPathRoot(filePath) ?? string.Empty;
            var drive = new DriveInfo(root);
            if (drive.DriveType == DriveType.Network)
                return new WritabilityReport(false, WritabilityIssue.NetworkOrVolume,
                    "File is on a network share. Check network permissions or copy locally.", []);
            if (drive.DriveType == DriveType.CDRom)
                return new WritabilityReport(false, WritabilityIssue.NetworkOrVolume,
                    "File is on read-only media (CD/DVD).", []);
        }
        catch { }

        return new WritabilityReport(false, WritabilityIssue.Unknown,
            "File cannot be written to. Try closing other applications that may have it open.", []);
    }

    // ── Try to open the file for writing (non-destructive) ────────────────────

    public static bool TryOpenForWrite(string filePath)
    {
        try
        {
            using var _ = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Windows Restart Manager — finds which processes lock a file ───────────
    // https://learn.microsoft.com/en-us/windows/win32/rstmgr/restart-manager-portal

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle,
        uint nFiles, string[] rgsFilenames,
        uint nApplications, [In] RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle,
        out uint pnProcInfoNeeded, ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    private const int RmRebootReasonNone = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string strServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public int TSSessionId;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    public static IReadOnlyList<ProcessInfo> GetLockingProcesses(string filePath)
    {
        var result = new List<ProcessInfo>();
        try
        {
            uint handle;
            string key = Guid.NewGuid().ToString();
            if (RmStartSession(out handle, 0, key) != 0) return result;

            try
            {
                if (RmRegisterResources(handle, 1, [filePath], 0, null, 0, null) != 0)
                    return result;

                uint pnProcInfoNeeded = 0, pnProcInfo = 0, lpdwRebootReasons = RmRebootReasonNone;
                RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, null, ref lpdwRebootReasons);

                if (pnProcInfoNeeded == 0) return result;

                pnProcInfo = pnProcInfoNeeded;
                var processInfo = new RM_PROCESS_INFO[pnProcInfo];
                if (RmGetList(handle, out pnProcInfoNeeded, ref pnProcInfo, processInfo, ref lpdwRebootReasons) != 0)
                    return result;

                for (int i = 0; i < pnProcInfo; i++)
                {
                    try
                    {
                        var proc = Process.GetProcessById(processInfo[i].Process.dwProcessId);
                        result.Add(new ProcessInfo(proc.Id, proc.ProcessName, processInfo[i].strAppName));
                    }
                    catch { /* process may have exited */ }
                }
            }
            finally
            {
                RmEndSession(handle);
            }
        }
        catch { /* RestartManager not available (Wine, old OS) */ }

        return result;
    }

    // ── Clear read-only + try to take ownership via attrib command ────────────

    public static async Task<bool> TryClearReadOnlyAsync(string filePath)
    {
        try
        {
            // Method 1: direct FileInfo
            var fi = new FileInfo(filePath);
            if (fi.IsReadOnly)
            {
                fi.IsReadOnly = false;
                if (!fi.IsReadOnly) return true;
            }
            else return true; // was already writable
        }
        catch { }

        // Method 2: attrib command (handles some edge cases with inherited attributes)
        try
        {
            var result = await Task.Run(() =>
            {
                var psi = new ProcessStartInfo("attrib", $"-R \"{filePath}\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow  = true,
                    RedirectStandardOutput = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(3000);
                return proc?.ExitCode == 0;
            });
            if (result)
            {
                await Task.Delay(100); // let FS flush
                return !new FileInfo(filePath).IsReadOnly;
            }
        }
        catch { }

        return false;
    }

    // ── Wait for a file to become writable, polling ───────────────────────────

    public static async Task<bool> WaitForWritableAsync(
        string filePath,
        int maxWaitMs = 5000,
        int pollMs    = 200)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxWaitMs)
        {
            if (TryOpenForWrite(filePath)) return true;
            await Task.Delay(pollMs);
        }
        return false;
    }

    // ── Human-readable helper ─────────────────────────────────────────────────

    public static string FriendlyError(WritabilityReport report) => report.Issue switch
    {
        WritabilityIssue.None                  => "File is writable.",
        WritabilityIssue.ReadOnlyAttribute     => "🔒 Read-only attribute — click Unlock to fix.",
        WritabilityIssue.LockedByThisApp       => "⏯ Held by preview player — stop preview then retry.",
        WritabilityIssue.LockedByProcess       => $"🔗 Locked by {string.Join(", ", report.LockingProcesses.Select(p => p.Name))}.",
        WritabilityIssue.InsufficientPermissions => "🚫 NTFS permission denied — try running as Administrator.",
        WritabilityIssue.NetworkOrVolume        => "🌐 Read-only volume or network share.",
        _                                       => "⚠ File cannot be written — check if another app has it open."
    };
}
