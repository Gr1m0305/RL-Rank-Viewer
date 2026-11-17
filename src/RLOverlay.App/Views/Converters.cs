using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RLOverlay.App.ViewModels;

namespace RLOverlay.App.Views;

/// <summary>true → Visible, false → Collapsed (pass "invert" as parameter to flip).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var on = value is true;
        if (parameter is "invert")
        {
            on = !on;
        }

        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Non-empty string / non-null → Visible, else Collapsed.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Adds letter spacing by putting hair spaces between characters (WPF TextBlock has no tracking).</summary>
public sealed class TrackingConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string s ? string.Join(" ", s.ToCharArray()) : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Win streak green, loss streak red, otherwise the normal number colour.</summary>
public sealed class StreakBrushConverter : IValueConverter
{
    public Brush Win { get; set; } = Brushes.LimeGreen;

    public Brush Loss { get; set; } = Brushes.IndianRed;

    public Brush Neutral { get; set; } = Brushes.White;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        StreakKind.Win => Win,
        StreakKind.Loss => Loss,
        _ => Neutral,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
