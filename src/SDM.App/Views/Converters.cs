using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SDM.App.Views;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Two-way "value == parameter" for radio buttons bound to one string or enum property:
/// IsChecked="{Binding Filter, Converter={StaticResource Equals}, ConverterParameter=active}".
/// </summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true) return Binding.DoNothing;
        if (targetType.IsEnum && parameter is string s) return Enum.Parse(targetType, s, ignoreCase: true);
        if (targetType == typeof(int) && parameter is string i) return int.Parse(i, CultureInfo.InvariantCulture);
        return parameter;
    }
}

/// <summary>0..1 fraction × parameter (height in px), for the connection map cells.</summary>
public sealed class FractionToHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double max = double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) ? m : 22;
        return value is double d ? Math.Clamp(d, 0, 1) * max : 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Collapsed when the value is null or an empty string.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || value is string { Length: 0 } ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
