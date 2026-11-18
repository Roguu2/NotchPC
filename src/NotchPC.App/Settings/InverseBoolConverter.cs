using System.Globalization;
using System.Windows.Data;

namespace NotchPC.App.Settings;

/// <summary>true ↔ false (e.g. disable a button while busy).</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}
