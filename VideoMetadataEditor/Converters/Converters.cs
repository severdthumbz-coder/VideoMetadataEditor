using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;

namespace VideoMetadataEditor.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        bool b = value is bool bl && bl;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

public class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        bool hasValue = value != null && (value is not string s || !string.IsNullOrWhiteSpace(s));
        if (Invert) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

public class BoolToStringConverter : IValueConverter
{
    public string TrueValue  { get; set; } = "True";
    public string FalseValue { get; set; } = "False";
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => (value is bool b && b) ? TrueValue : FalseValue;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

public class BoolToBrushConverter : IValueConverter
{
    public WpfBrush TrueBrush  { get; set; } = WpfBrushes.Green;
    public WpfBrush FalseBrush { get; set; } = WpfBrushes.Red;
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => (value is bool b && b) ? TrueBrush : FalseBrush;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

public class InvertBoolConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is bool b ? !b : false;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => v is bool b ? !b : false;
}

public class VideoFormatColorConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is bool hasError && hasError)
            return new SolidColorBrush(Color.FromRgb(220, 80, 80));
        return new SolidColorBrush(Color.FromRgb(80, 200, 120));
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

public class BytesToImageSourceConverter : IValueConverter
{
    public object? Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not byte[] { Length: > 0 } bytes) return null;
        try
        {
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.StreamSource   = new System.IO.MemoryStream(bytes);
            img.CacheOption    = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch { return null; }
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

public class FileSizeColorConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        // Just returns the themed foreground; real theming handled by theme dict
        return SystemColors.ControlTextBrush;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>Converts nullable bool to Visibility: null=Collapsed, bool value=Visible.</summary>
public class NullableBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is bool ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>
/// Converts a string property to bool by comparing against ConverterParameter.
/// Used for RadioButton binding to enum-like string settings.
/// TwoWay: sets the property to ConverterParameter when the RadioButton is checked.
/// </summary>
public class StringEqualsBoolConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => value is string s && p is string param && s == param;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c)
        => (value is bool b && b && p is string param) ? param : Binding.DoNothing;
}

[ValueConversion(typeof(string), typeof(string))]
public class PathToFilenameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string path && !string.IsNullOrWhiteSpace(path))
        {
            var name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
        return value ?? string.Empty;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Collapses when int value is 0. Used to hide empty ListBoxes.</summary>
[ValueConversion(typeof(int), typeof(Visibility))]
public class ZeroToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
