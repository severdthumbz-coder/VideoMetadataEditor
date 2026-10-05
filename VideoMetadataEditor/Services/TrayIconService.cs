using System.IO;
using WinForms = System.Windows.Forms;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Owns the system-tray icon and its context menu.
///
/// WPF has no native tray support, so this uses System.Windows.Forms.NotifyIcon via a
/// FrameworkReference (see the .csproj). Every WinForms type here is explicitly
/// qualified through the "WinForms" alias — UseWindowsForms is deliberately NOT set,
/// because it would make Application/MessageBox ambiguous across the codebase.
///
/// The menu is deliberately limited to actions that make sense with no window on
/// screen: restore, rescan, toggle watching, start-with-Windows, exit. Operations
/// whose whole point is inspecting results (Duplicates, Health Check) or that need a
/// UI selection (Copy/Move Selected) are excluded — from a hidden window they would
/// either run invisibly or force the window back open, defeating the purpose.
///
/// This class is UI-shell only: it raises events and lets the host decide what to do.
/// It never disposes app state itself.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private WinForms.NotifyIcon? _icon;
    private WinForms.ToolStripMenuItem? _watchItem;
    private WinForms.ToolStripMenuItem? _startupItem;
    private bool _balloonShown;

    /// <summary>Raised when the user asks to restore the main window.</summary>
    public event Action? RestoreRequested;
    /// <summary>Raised when the user asks to rescan the watched folder(s).</summary>
    public event Action? RescanRequested;
    /// <summary>Raised when the user toggles watching. Argument = requested state.</summary>
    public event Action<bool>? WatchToggleRequested;
    /// <summary>Raised when the user toggles start-with-Windows. Argument = requested state.</summary>
    public event Action<bool>? StartWithWindowsToggleRequested;
    /// <summary>Raised when the user chooses Exit — the host must perform a real shutdown.</summary>
    public event Action? ExitRequested;

    public bool IsVisible => _icon?.Visible == true;

    /// <summary>
    /// The running build's version (e.g. "1.4.0.162"), read from the assembly so each
    /// build labels itself — useful when several builds are open side by side.
    /// AssemblyVersion is set from &lt;FullVersion&gt; in the csproj.
    /// </summary>
    private static readonly string BuildVersion =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    /// <summary>"Video Metadata Editor v1.4.0.162" — 32 chars, well under the tooltip limit.</summary>
    private static readonly string AppLabel = $"Video Metadata Editor v{BuildVersion}";

    /// <summary>
    /// Creates and shows the tray icon. Safe to call more than once; subsequent calls
    /// are ignored while an icon already exists.
    /// </summary>
    public void Show(bool watchEnabled, bool startWithWindows)
    {
        if (_icon != null) { _icon.Visible = true; return; }

        _icon = new WinForms.NotifyIcon
        {
            Icon    = LoadAppIcon(),
            Text    = AppLabel,   // NotifyIcon tooltip caps at 63 chars; AppLabel is ~32
            Visible = true
        };

        // Double-click the icon = restore, matching standard tray behaviour.
        _icon.DoubleClick += (_, _) => RestoreRequested?.Invoke();

        var menu = new WinForms.ContextMenuStrip();

        // Non-clickable header naming the exact build, so with several builds in the
        // tray you can tell which one's menu you've opened.
        var header = new WinForms.ToolStripMenuItem(AppLabel) { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        var restore = new WinForms.ToolStripMenuItem("Restore window");
        restore.Click += (_, _) => RestoreRequested?.Invoke();
        // Bold it: it's the default action, matching the double-click.
        restore.Font = new System.Drawing.Font(restore.Font, System.Drawing.FontStyle.Bold);
        menu.Items.Add(restore);

        var rescan = new WinForms.ToolStripMenuItem("Rescan watched folder(s)");
        rescan.Click += (_, _) => RescanRequested?.Invoke();
        menu.Items.Add(rescan);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        _watchItem = new WinForms.ToolStripMenuItem("Watching")
        {
            CheckOnClick = true,
            Checked      = watchEnabled
        };
        _watchItem.CheckedChanged += (_, _) =>
            WatchToggleRequested?.Invoke(_watchItem!.Checked);
        menu.Items.Add(_watchItem);

        _startupItem = new WinForms.ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked      = startWithWindows
        };
        _startupItem.CheckedChanged += (_, _) =>
            StartWithWindowsToggleRequested?.Invoke(_startupItem!.Checked);
        menu.Items.Add(_startupItem);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var exit = new WinForms.ToolStripMenuItem($"Exit v{BuildVersion}");
        exit.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exit);

        _icon.ContextMenuStrip = menu;
    }

    /// <summary>Hides the tray icon without disposing it.</summary>
    public void Hide()
    {
        if (_icon != null) _icon.Visible = false;
    }

    /// <summary>
    /// Shows the one-time "still running in the tray" balloon, so the window
    /// disappearing doesn't look like the app quit. Only ever shown once per session.
    /// </summary>
    public void ShowFirstMinimiseHint()
    {
        if (_icon == null || _balloonShown) return;
        _balloonShown = true;
        try
        {
            _icon.BalloonTipTitle = "Still running";
            _icon.BalloonTipText  = $"{AppLabel} is in the tray and still watching. " +
                                    "Double-click the icon to restore, or right-click for Exit.";
            _icon.BalloonTipIcon  = WinForms.ToolTipIcon.Info;
            _icon.ShowBalloonTip(4000);
        }
        catch { /* balloons can be suppressed by OS policy — non-fatal */ }
    }

    /// <summary>Reflects external state changes back into the menu's checkmarks.</summary>
    public void SyncWatchState(bool watchEnabled)
    {
        if (_watchItem != null && _watchItem.Checked != watchEnabled)
            _watchItem.Checked = watchEnabled;
    }

    public void SyncStartupState(bool startWithWindows)
    {
        if (_startupItem != null && _startupItem.Checked != startWithWindows)
            _startupItem.Checked = startWithWindows;
    }

    /// <summary>
    /// Loads the packed application icon for the tray. Falls back to the running EXE's
    /// own icon, then to the system default, so a missing resource never throws.
    /// </summary>
    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/app.ico", UriKind.Absolute);
            var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream != null)
            {
                using (stream)
                    return new System.Drawing.Icon(stream);
            }
        }
        catch { /* fall through */ }

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (extracted != null) return extracted;
            }
        }
        catch { /* fall through */ }

        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_icon == null) return;
        _icon.Visible = false;                 // remove from tray immediately
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _icon = null;
    }
}
