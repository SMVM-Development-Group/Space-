using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SpacePlusPlus.Infrastructure;

namespace SpacePlusPlus.Converters;

public class BytesToSizeConverter : IValueConverter
{
    public static readonly BytesToSizeConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is long l ? SizeFormat.Format(l) : value is int i ? SizeFormat.Format(i) : "—";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public static readonly BoolToVisibilityConverter Instance = new();
    public bool Inverted { get; set; }
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (Inverted) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility vis && vis == Visibility.Visible;
}

public class NullToVisibilityConverter : IValueConverter
{
    public static readonly NullToVisibilityConverter Instance = new();
    public bool Inverted { get; set; }
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Empty strings count as "no value" — view models commonly return string.Empty
        // instead of null for cleared state.
        bool hasValue = value is not null && (value is not string s || s.Length > 0);
        if (Inverted) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class CountToVisibilityConverter : IValueConverter
{
    public static readonly CountToVisibilityConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is long l && l > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class BytesToShareTextConverter : IMultiValueConverter
{
    public static readonly BytesToShareTextConverter Instance = new();
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is long bytes && values[1] is long total && total > 0)
            return $"{bytes * 100.0 / total:F1}%";
        return "—";
    }
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class IconKeyConverter : IValueConverter
{
    public static readonly IconKeyConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string key && !string.IsNullOrEmpty(key))
        {
            var geometry = Application.Current?.TryFindResource("Icon" + key);
            if (geometry is System.Windows.Media.Geometry g) return g;
        }
        var fallback = Application.Current?.TryFindResource("IconFolder");
        return fallback is System.Windows.Media.Geometry fg ? fg : System.Windows.Media.Geometry.Empty;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class BoolToYesNoConverter : IValueConverter
{
    public static readonly BoolToYesNoConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? "Yes" : "No";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public class BytesPerSecondConverter : IMultiValueConverter
{
    public static readonly BytesPerSecondConverter Instance = new();
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length >= 2 && values[0] is long bytes && values[1] is TimeSpan t && t.TotalSeconds > 0.5)
            return SizeFormat.Format((long)(bytes / t.TotalSeconds)) + "/s";
        return "—";
    }
    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
