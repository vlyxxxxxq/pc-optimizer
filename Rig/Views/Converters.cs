using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Rig.Views;

/// <summary>true → Collapsed, false → Visible.</summary>
public sealed class InverseBoolToVisibility : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not Visibility.Visible;
}

/// <summary>Flips a bool.</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
