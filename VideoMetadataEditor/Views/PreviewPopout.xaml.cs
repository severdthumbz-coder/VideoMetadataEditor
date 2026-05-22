using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VideoMetadataEditor.Views;

public partial class PreviewPopout : Window
{
    private bool _isPlaying;
    private string? _filePath;

    public PreviewPopout(string filePath, string title)
    {
        InitializeComponent();
        _filePath = filePath;
        Title = $"Preview — {title}";
        TitleLabel.Text = title;
        Player.Volume = VolumeSlider.Value;
    }

    // ── Playback ──────────────────────────────────────────────────────────────

    public void StartPlayback()
    {
        if (_filePath == null) return;
        if (Player.Source == null ||
            Player.Source.LocalPath != _filePath)
            Player.Source = new Uri(_filePath);

        Player.Play();
        _isPlaying = true;
        PlayPauseBtn.Content = "⏸ Pause";
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_filePath == null) return;
        if (_isPlaying)
        {
            Player.Pause();
            _isPlaying = false;
            PlayPauseBtn.Content = "▶ Play";
        }
        else
        {
            if (Player.Source == null)
                Player.Source = new Uri(_filePath);
            Player.Play();
            _isPlaying = true;
            PlayPauseBtn.Content = "⏸ Pause";
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        Player.Stop();
        Player.Source = null;
        _isPlaying = false;
        PlayPauseBtn.Content = "▶ Play";
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Stop();
        _isPlaying = false;
        PlayPauseBtn.Content = "▶ Play";
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        => Player.Volume = e.NewValue;

    // ── Window lifecycle ──────────────────────────────────────────────────────

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        Player.Stop();
        Player.Source = null;
    }

    /// <summary>Called by MainWindow when the main player starts — stop the popout to avoid dual audio.</summary>
    public void StopForMainPlayer()
    {
        Player.Stop();
        Player.Source = null;
        _isPlaying = false;
        PlayPauseBtn.Content = "▶ Play";
    }
}
