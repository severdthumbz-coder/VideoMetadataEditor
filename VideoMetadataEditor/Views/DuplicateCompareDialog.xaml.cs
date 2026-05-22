using System.IO;
using System.Windows;
using System.Windows.Threading;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Views;

/// <summary>
/// Side-by-side video comparison dialog for duplicate files.
/// Loads two files into independent MediaElement players.
/// Both play/pause/stop together; delete buttons remove one and close.
/// </summary>
public partial class DuplicateCompareDialog : Window
{
    private readonly VideoFile _leftFile;
    private readonly VideoFile _rightFile;
    private readonly DispatcherTimer _syncTimer;

    // Result: null = skipped, true = delete left, false = delete right
    public bool?   DeleteLeft { get; private set; }
    public string? DeletedPath { get; private set; }

    public DuplicateCompareDialog(VideoFile leftFile, VideoFile rightFile)
    {
        InitializeComponent();

        _leftFile  = leftFile;
        _rightFile = rightFile;

        // Populate info strips
        LeftFileName.Text  = Path.GetFileName(leftFile.FilePath);
        RightFileName.Text = Path.GetFileName(rightFile.FilePath);
        LeftFileInfo.Text  = BuildInfo(leftFile);
        RightFileInfo.Text = BuildInfo(rightFile);
        LeftLabel.Text     = "LEFT — " + FormatSize(leftFile.FileSizeBytes);
        RightLabel.Text    = "RIGHT — " + FormatSize(rightFile.FileSizeBytes);

        // Highlight the larger (likely higher quality) file in green
        if (leftFile.FileSizeBytes > rightFile.FileSizeBytes)
            LeftSelectedBorder.Visibility = Visibility.Visible;
        else if (rightFile.FileSizeBytes > leftFile.FileSizeBytes)
            RightSelectedBorder.Visibility = Visibility.Visible;

        // Load both players
        PlayerLeft.Source  = new Uri(leftFile.FilePath);
        PlayerRight.Source = new Uri(rightFile.FilePath);
        PlayerLeft.Volume  = 0.7;
        PlayerRight.Volume = 0;   // right muted by default — avoid audio clash

        PlayerLeft.Play();
        PlayerRight.Play();

        // Sync timer — keeps both players at roughly the same position
        _syncTimer = new DispatcherTimer(TimeSpan.FromSeconds(5),
            DispatcherPriority.Background, SyncPlayers, Dispatcher);
        _syncTimer.Start();

        Closed += (_, _) => { _syncTimer.Stop(); StopBoth(); };
        ApplyTheme();
    }

    // ── Transport ─────────────────────────────────────────────────────────────

    private void PlayBoth_Click(object s, RoutedEventArgs e)
    {
        PlayerLeft.Play();
        PlayerRight.Play();
    }

    private void PauseBoth_Click(object s, RoutedEventArgs e)
    {
        PlayerLeft.Pause();
        PlayerRight.Pause();
    }

    private void StopBoth_Click(object s, RoutedEventArgs e) => StopBoth();
    private void StopBoth()
    {
        PlayerLeft.Stop();
        PlayerRight.Stop();
    }

    private void VolumeSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (PlayerLeft != null)  PlayerLeft.Volume  = e.NewValue;
        if (PlayerRight != null) PlayerRight.Volume = 0; // keep right muted
    }

    private void SyncPlayers(object? s, EventArgs e)
    {
        // If one player falls significantly behind, seek the other to match
        if (PlayerLeft.NaturalDuration.HasTimeSpan
            && PlayerRight.NaturalDuration.HasTimeSpan)
        {
            var diff = (PlayerLeft.Position - PlayerRight.Position).Duration();
            if (diff > TimeSpan.FromSeconds(2))
                PlayerRight.Position = PlayerLeft.Position;
        }
    }

    // ── Player events ─────────────────────────────────────────────────────────

    private void Player_MediaOpened(object s, RoutedEventArgs e) { /* ready */ }
    private void Player_MediaEnded(object s, RoutedEventArgs e)
    {
        // Loop both
        if (s is System.Windows.Controls.MediaElement me)
        {
            me.Position = TimeSpan.Zero;
            me.Play();
        }
    }
    private void Player_MediaFailed(object s, ExceptionRoutedEventArgs e)
    {
        // One player failed — show error inline rather than crashing
        if (s == PlayerLeft)  LeftFileInfo.Text  = $"⚠ Playback failed: {e.ErrorException?.Message}";
        if (s == PlayerRight) RightFileInfo.Text = $"⚠ Playback failed: {e.ErrorException?.Message}";
    }

    // ── Delete / Skip ─────────────────────────────────────────────────────────

    private void DeleteLeft_Click(object s, RoutedEventArgs e)
    {
        if (ConfirmDelete(_leftFile.FilePath))
        {
            DeleteLeft   = true;
            DeletedPath  = _leftFile.FilePath;
            DialogResult = true;
            Close();
        }
    }

    private void DeleteRight_Click(object s, RoutedEventArgs e)
    {
        if (ConfirmDelete(_rightFile.FilePath))
        {
            DeleteLeft   = false;
            DeletedPath  = _rightFile.FilePath;
            DialogResult = true;
            Close();
        }
    }

    private void Skip_Click(object s, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private bool ConfirmDelete(string path)
    {
        var result = MessageBox.Show(
            $"Delete this file?\n\n{Path.GetFileName(path)}\n({FormatSize(new FileInfo(path).Length)})\n\nThis moves the file to the Recycle Bin.",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return result == MessageBoxResult.Yes;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildInfo(VideoFile vf)
    {
        var parts = new List<string>();
        parts.Add(FormatSize(vf.FileSizeBytes));
        // Format is a VideoFormat enum — convert to string
        if (vf.Format != Models.VideoFormat.Unknown)
            parts.Add(vf.Format.ToString().ToUpperInvariant());
        // Resolution lives on the DuplicatesViewModel / MediaInfo — use FileName as fallback
        var meta = vf.EmbeddedMetadata;
        if (!string.IsNullOrWhiteSpace(meta?.Title))     parts.Add(meta!.Title);
        if (!string.IsNullOrWhiteSpace(meta?.Year))      parts.Add(meta!.Year);
        if (meta?.IsEpisode == true)
            parts.Add($"S{meta.Season:D2}E{meta.Episode:D2}");
        return string.Join("  ·  ", parts);
    }

    private static string FormatSize(long b)
        => b >= 1073741824L ? $"{b / 1073741824.0:F2} GB"
         : b >= 1048576L    ? $"{b / 1048576.0:F1} MB"
                            : $"{b / 1024.0:F1} KB";

    private void ApplyTheme()
    {
        // Inherit theme from main window
        var owner = Owner;
        if (owner?.Resources is ResourceDictionary rd)
            foreach (var dict in rd.MergedDictionaries)
                Resources.MergedDictionaries.Add(dict);
    }
}
