using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;

namespace VideoMetadataEditor.Views;

public partial class SplashScreen : Window
{
    private readonly DispatcherTimer _progressTimer = new();
    private int _tick;

    public SplashScreen()
    {
        InitializeComponent();

        // Show version info
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v != null
            ? $"Version {v.Major}.{v.Minor}.{v.Build}  ·  Build {v.Revision}"
            : "Version 1.0.0  ·  Build 1";

        // Animate the progress bar to fill over ~2 seconds (40 ticks × 50 ms)
        _progressTimer.Interval = TimeSpan.FromMilliseconds(50);
        _progressTimer.Tick += (_, _) =>
        {
            _tick++;
            SplashProgress.Value = Math.Min(_tick * 2.5, 100);
            if (_tick >= 40) _progressTimer.Stop();
        };
        _progressTimer.Start();
    }

    private void Attribution_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* browser unavailable — silently ignore */ }
        e.Handled = true;
    }
}
