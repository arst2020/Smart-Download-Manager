using System.Windows;
using Microsoft.Win32;
using SDM.Core.Settings;

namespace SDM.App.Themes;

/// <summary>Swaps the palette dictionary (Dark.xaml / Light.xaml) at runtime.</summary>
public static class ThemeManager
{
    private static ResourceDictionary? _current;

    public static bool IsDark { get; private set; } = true;

    public static void Apply(AppTheme theme)
    {
        bool dark = theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => !WindowsUsesLightTheme()
        };
        IsDark = dark;

        var palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/SmartDownloadManager;component/Themes/{(dark ? "Dark" : "Light")}.xaml")
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_current is not null) merged.Remove(_current);
        merged.Insert(0, palette); // before Controls.xaml so styles resolve the new brushes
        _current = palette;
    }

    /// <summary>Windows "Choose your app mode" setting; dark if it cannot be read.</summary>
    public static bool WindowsUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }
}
