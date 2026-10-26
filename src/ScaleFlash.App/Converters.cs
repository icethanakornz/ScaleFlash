using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ScaleFlash.App;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        bool hasValue = value is not null && (value is not string s || s.Length > 0);
        return hasValue ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Severity used for chips and job states.</summary>
public enum StatusKind { Neutral, Ok, Busy, Warning, Error }

public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value switch
        {
            StatusKind.Ok => "SystemFillColorSuccessBrush",
            StatusKind.Busy => "AccentTextFillColorPrimaryBrush",
            StatusKind.Warning => "SystemFillColorCautionBrush",
            StatusKind.Error => "SystemFillColorCriticalBrush",
            _ => "TextFillColorSecondaryBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StatusToBackgroundConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value switch
        {
            StatusKind.Ok => "SystemFillColorSuccessBackgroundBrush",
            StatusKind.Busy => "SystemFillColorAttentionBackgroundBrush",
            StatusKind.Warning => "SystemFillColorCautionBackgroundBrush",
            StatusKind.Error => "SystemFillColorCriticalBackgroundBrush",
            _ => "ControlFillColorSecondaryBrush",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
