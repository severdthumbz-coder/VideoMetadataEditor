using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace VideoMetadataEditor.Views;

public enum BannerType { Success, Error, Warning, Info, Copy, Move }

public class BannerChip
{
    public string Icon  { get; init; } = "";
    public string Label { get; init; } = "";
}

public partial class CompletionBanner : UserControl
{
    private static readonly Dictionary<BannerType, (Color from, Color to)> Palettes = new()
    {
        [BannerType.Success] = (Color.FromRgb(0x4C, 0xAF, 0x82), Color.FromRgb(0x1A, 0x7A, 0x50)),
        [BannerType.Error]   = (Color.FromRgb(0xEF, 0x53, 0x50), Color.FromRgb(0xB7, 0x1C, 0x1C)),
        [BannerType.Warning] = (Color.FromRgb(0xFF, 0xA7, 0x26), Color.FromRgb(0xE6, 0x51, 0x00)),
        [BannerType.Info]    = (Color.FromRgb(0x4F, 0xC3, 0xF7), Color.FromRgb(0x01, 0x57, 0x9B)),
        // Copy  — ocean teal: clearly distinct from Success green
        [BannerType.Copy]    = (Color.FromRgb(0x00, 0xBB, 0xD4), Color.FromRgb(0x00, 0x60, 0x6E)),
        // Move  — royal violet: clearly distinct from all others
        [BannerType.Move]    = (Color.FromRgb(0x7C, 0x4D, 0xFF), Color.FromRgb(0x43, 0x1E, 0xC5)),
    };

    private static readonly Dictionary<BannerType, string> Icons = new()
    {
        [BannerType.Success] = "✓",
        [BannerType.Error]   = "✕",
        [BannerType.Warning] = "⚠",
        [BannerType.Info]    = "ℹ",
        [BannerType.Copy]    = "⧉",   // copy/duplicate symbol
        [BannerType.Move]    = "↗",   // move/transfer symbol
    };

    private const double BannerHeight = 88;

    private readonly DispatcherTimer _countdown = new();
    private double _countdownSeconds;
    private double _elapsed;

    public CompletionBanner()
    {
        InitializeComponent();

        _countdown.Interval = TimeSpan.FromMilliseconds(30);
        _countdown.Tick += OnCountdownTick;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Show(string title, string body, BannerType type = BannerType.Success,
                     double autoHideSeconds = 5)
        => ShowCore(title, body, type, autoHideSeconds, []);

    public void ShowRich(string title, string body, BannerType type,
                         double autoHideSeconds, params BannerChip[] chips)
        => ShowCore(title, body, type, autoHideSeconds, chips);

    private void ShowCore(string title, string body, BannerType type,
                          double autoHideSeconds, BannerChip[] chips)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ShowCore(title, body, type, autoHideSeconds, chips));
            return;
        }

        _countdown.Stop();
        StopAnimations();

        // Colours
        var (from, to) = Palettes[type];
        GradStop1.Color = from;
        GradStop2.Color = to;

        // Text
        BannerIcon.Text  = Icons[type];
        BannerTitle.Text = title;
        BannerBody.Text  = body;
        BannerBody.Visibility = string.IsNullOrWhiteSpace(body)
            ? Visibility.Collapsed : Visibility.Visible;

        // Chips
        var chipControls = new[] {
            (Chip1, Chip1Icon, Chip1Text),
            (Chip2, Chip2Icon, Chip2Text),
            (Chip3, Chip3Icon, Chip3Text)
        };
        foreach (var (b, _, _) in chipControls) b.Visibility = Visibility.Collapsed;
        for (int i = 0; i < Math.Min(chips.Length, 3); i++)
        {
            var (b, icon, text) = chipControls[i];
            icon.Text = chips[i].Icon;
            text.Text = chips[i].Label;
            b.Visibility = Visibility.Visible;
        }

        // Reset countdown bar
        _elapsed = 0;
        _countdownSeconds = autoHideSeconds;
        CountdownBar.Width = 0;

        // Show and animate in
        Visibility = Visibility.Visible;
        IsHitTestVisible = true;

        AnimateIn();

        if (autoHideSeconds > 0) _countdown.Start();
    }

    public void Hide()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(Hide); return; }
        _countdown.Stop();
        AnimateOut();
    }

    // ── Animations (pure code — no XAML storyboards) ─────────────────────────

    private void AnimateIn()
    {
        // Slide down: UserControl Height 0 → 88
        var heightAnim = new DoubleAnimation(0, BannerHeight, new Duration(TimeSpan.FromMilliseconds(350)))
        {
            EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 }
        };
        BeginAnimation(HeightProperty, heightAnim);

        // Fade in content
        var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(250)))
        {
            BeginTime = TimeSpan.FromMilliseconds(100)
        };
        BannerContent.BeginAnimation(OpacityProperty, fadeIn);
    }

    private void AnimateOut()
    {
        IsHitTestVisible = false;

        // Fade out content first
        var fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(150)));
        BannerContent.BeginAnimation(OpacityProperty, fadeOut);

        // Slide up: Height 88 → 0
        var heightAnim = new DoubleAnimation(BannerHeight, 0, new Duration(TimeSpan.FromMilliseconds(280)))
        {
            BeginTime    = TimeSpan.FromMilliseconds(50),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        heightAnim.Completed += (_, _) => Visibility = Visibility.Collapsed;
        BeginAnimation(HeightProperty, heightAnim);
    }

    private void StopAnimations()
    {
        BeginAnimation(HeightProperty, null);
        BannerContent.BeginAnimation(OpacityProperty, null);
    }

    // ── Countdown bar ─────────────────────────────────────────────────────────

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _elapsed += _countdown.Interval.TotalSeconds;
        var pct = Math.Min(_elapsed / _countdownSeconds, 1.0);
        var w = GradientBg.ActualWidth > 0 ? GradientBg.ActualWidth : ActualWidth;
        CountdownBar.Width = w * pct;

        if (_elapsed >= _countdownSeconds)
        {
            _countdown.Stop();
            Hide();
        }
    }

    // ── Dismiss ───────────────────────────────────────────────────────────────

    private void DismissBtn_Click(object sender, RoutedEventArgs e) => Hide();
}
