using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfPanel = System.Windows.Controls.Panel;

namespace VideoMetadataEditor.Converters;

/// <summary>
/// Attached property to show placeholder "watermark" text inside a TextBox
/// when it is empty and unfocused. Works reliably in WPF without VisualBrush.
/// Usage: <TextBox conv:WatermarkHelper.Watermark="Search…"/>
/// </summary>
public static class WatermarkHelper
{
    public static readonly DependencyProperty WatermarkProperty =
        DependencyProperty.RegisterAttached(
            "Watermark", typeof(string), typeof(WatermarkHelper),
            new PropertyMetadata(string.Empty, OnWatermarkChanged));

    public static string GetWatermark(DependencyObject obj) => (string)obj.GetValue(WatermarkProperty);
    public static void SetWatermark(DependencyObject obj, string value) => obj.SetValue(WatermarkProperty, value);

    private static void OnWatermarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;
        tb.Loaded            -= Tb_Refresh;
        tb.TextChanged       -= Tb_Refresh;
        tb.GotFocus          -= Tb_Refresh;
        tb.LostFocus         -= Tb_Refresh;
        tb.IsEnabledChanged  -= Tb_EnabledChanged;
        tb.Loaded            += Tb_Refresh;
        tb.TextChanged       += Tb_Refresh;
        tb.GotFocus          += Tb_Refresh;
        tb.LostFocus         += Tb_Refresh;
        tb.IsEnabledChanged  += Tb_EnabledChanged;
    }

    private static void Tb_EnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox tb) Tb_Refresh(tb, new RoutedEventArgs());
    }

    private static void Tb_Refresh(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var watermark = GetWatermark(tb);
        if (string.IsNullOrWhiteSpace(watermark)) return;

        // Use AdornerLayer for clean overlay
        var layer = System.Windows.Documents.AdornerLayer.GetAdornerLayer(tb);
        if (layer == null) return;

        // Remove existing adorners of our type
        var adorners = layer.GetAdorners(tb);
        if (adorners != null)
            foreach (var a in adorners.OfType<WatermarkAdorner>())
                layer.Remove(a);

        if (string.IsNullOrEmpty(tb.Text) && !tb.IsFocused)
            layer.Add(new WatermarkAdorner(tb, watermark));
    }
}

internal class WatermarkAdorner : System.Windows.Documents.Adorner
{
    private readonly string _text;
    public WatermarkAdorner(UIElement adornedElement, string text) : base(adornedElement) { _text = text; IsHitTestVisible = false; }

    protected override void OnRender(DrawingContext dc)
    {
        if (AdornedElement is not TextBox tb) return;
        var brush = tb.TryFindResource("ForegroundMutedBrush") as Brush ?? Brushes.Gray;
        var ft = new FormattedText(_text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 13, brush,
            VisualTreeHelper.GetDpi(tb).PixelsPerDip);
        dc.DrawText(ft, new Point(10, (tb.ActualHeight - ft.Height) / 2));
    }
}

/// <summary>
/// Attached property that adds uniform spacing between children of a Panel.
/// Mimics WinUI's StackPanel.Spacing for WPF.
/// Usage: <StackPanel conv:SpacingHelper.Spacing="8"/>
/// </summary>
public static class SpacingHelper
{
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.RegisterAttached(
            "Spacing", typeof(double), typeof(SpacingHelper),
            new PropertyMetadata(0.0, OnSpacingChanged));

    public static double GetSpacing(DependencyObject o) => (double)o.GetValue(SpacingProperty);
    public static void SetSpacing(DependencyObject o, double v) => o.SetValue(SpacingProperty, v);

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not WpfPanel panel) return;
        ApplySpacing(panel, (double)e.NewValue);
        panel.Loaded -= Panel_Loaded;
        panel.Loaded += Panel_Loaded;
    }

    private static void Panel_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is WpfPanel panel) ApplySpacing(panel, GetSpacing(panel));
    }

    private static void ApplySpacing(WpfPanel panel, double spacing)
    {
        bool horizontal = panel is StackPanel sp && sp.Orientation == Orientation.Horizontal;
        for (int i = 0; i < panel.Children.Count; i++)
        {
            if (panel.Children[i] is not FrameworkElement fe) continue;
            if (i == 0) continue; // no margin before first
            var m = fe.Margin;
            if (horizontal)
                fe.Margin = new Thickness(spacing, m.Top, m.Right, m.Bottom);
            else
                fe.Margin = new Thickness(m.Left, spacing, m.Right, m.Bottom);
        }
    }
}
