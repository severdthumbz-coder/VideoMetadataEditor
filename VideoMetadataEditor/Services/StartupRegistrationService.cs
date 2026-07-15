using Microsoft.Win32;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Registers/unregisters the app to start with Windows, via the per-user Run key.
///
/// Portable-app caveat: this EXE can be moved or renamed at any time, so a stored
/// path can go stale. Everything here therefore works off the CURRENT executable
/// path, and <see cref="Refresh"/> re-points a stale entry on startup rather than
/// leaving Windows trying to launch a file that has moved.
///
/// Per-user (HKCU) only — never HKLM. No admin rights needed, and it can't affect
/// other users of the machine.
/// </summary>
public static class StartupRegistrationService
{
    private const string RunKey     = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName  = "VideoMetadataEditor";

    /// <summary>The current executable's full path, or null if it can't be determined.</summary>
    public static string? CurrentExePath => Environment.ProcessPath;

    /// <summary>
    /// True when a Run entry exists for this app AND it points at the executable that
    /// is running right now. A stale entry (app moved) reports false, because as far as
    /// this build is concerned it is not registered.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            var stored = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(stored)) return false;
            var exe = CurrentExePath;
            if (string.IsNullOrWhiteSpace(exe)) return false;
            return PathsMatch(stored, exe);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Enables or disables start-with-Windows for the current user. Returns true on
    /// success. Never throws — registry access can be blocked by policy.
    /// </summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key == null) return false;

            if (enabled)
            {
                var exe = CurrentExePath;
                if (string.IsNullOrWhiteSpace(exe)) return false;
                // Quote the path: it will contain spaces on most installs.
                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else
            {
                if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// If a Run entry exists but points somewhere other than the running executable,
    /// re-point it. Call once on startup so moving the portable EXE doesn't silently
    /// break auto-start. Does nothing when no entry exists (i.e. feature is off).
    /// Returns true if a stale entry was corrected.
    /// </summary>
    public static bool Refresh()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            var stored = key?.GetValue(ValueName) as string;
            if (key == null || string.IsNullOrWhiteSpace(stored)) return false;

            var exe = CurrentExePath;
            if (string.IsNullOrWhiteSpace(exe)) return false;
            if (PathsMatch(stored, exe)) return false;

            key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Compares a stored Run value (possibly quoted) against a real path.</summary>
    private static bool PathsMatch(string storedValue, string exePath)
    {
        var cleaned = storedValue.Trim().Trim('"');
        return string.Equals(cleaned, exePath, StringComparison.OrdinalIgnoreCase);
    }
}
