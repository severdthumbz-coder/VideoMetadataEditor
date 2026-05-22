using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace VideoMetadataEditor.Views;

public enum ToastType { Success, Error, Info, Warning }

public partial class ToastNotification : UserControl
{
    private readonly DispatcherTimer _autoHideTimer = new();

    public ToastNotification()
    {
        InitializeComponent();

        _autoHideTimer.Tick += (_, _) =>
        {
            _autoHideTimer.Stop();
            Hide();
        };
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void Show(string title, string body, ToastType type = ToastType.Success, int autoHideSeconds = 4)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Show(title, body, type, autoHideSeconds));
            return;
        }

        ToastTitle.Text = title;
        ToastBody.Text  = body;
        ToastBody.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;

        ApplyType(type);

        _autoHideTimer.Stop();
        Visibility = Visibility.Visible;
        IsHitTestVisible = true;

        // Slide in from bottom-right using pure code animation
        AnimateIn();

        if (autoHideSeconds > 0)
        {
            _autoHideTimer.Interval = TimeSpan.FromSeconds(autoHideSeconds);
            _autoHideTimer.Start();
        }
    }

    public void Hide()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(Hide); return; }
        _autoHideTimer.Stop();
        IsHitTestVisible = false;
        AnimateOut();
    }

    // ── Animations (pure code) ────────────────────────────────────────────────

    private void AnimateIn()
    {
        // Fade in
        var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)));
        BeginAnimation(OpacityProperty, fade);

        // Slide up from bottom
        var slide = new ThicknessAnimation(
            new Thickness(0, 0, 16, -80),
            new Thickness(0, 0, 16, 16),
            new Duration(TimeSpan.FromMilliseconds(250)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        BeginAnimation(MarginProperty, slide);
    }

    private void AnimateOut()
    {
        var fade = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(220)));
        BeginAnimation(OpacityProperty, fade);

        var slide = new ThicknessAnimation(
            new Thickness(0, 0, 16, 16),
            new Thickness(0, 0, 16, -80),
            new Duration(TimeSpan.FromMilliseconds(220)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        slide.Completed += (_, _) => Visibility = Visibility.Collapsed;
        BeginAnimation(MarginProperty, slide);
    }

    // ── Type styling ──────────────────────────────────────────────────────────

    private void ApplyType(ToastType type)
    {
        var (icon, brushKey) = type switch
        {
            ToastType.Success => ("✅", "SuccessBrush"),
            ToastType.Error   => ("❌", "ErrorBrush"),
            ToastType.Warning => ("⚠️", "WarningBrush"),
            ToastType.Info    => ("ℹ️", "AccentBrush"),
            _                 => ("✅", "SuccessBrush")
        };

        ToastIcon.Text = icon;
        if (TryFindResource(brushKey) is Brush brush)
            AccentBar.Background = brush;
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e) => Hide();
}
