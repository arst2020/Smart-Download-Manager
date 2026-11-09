using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows;

namespace SDM.App.Localization;

/// <summary>
/// Bindable string table. XAML: {Binding [Key], Source={x:Static loc:Loc.Instance}}.
/// Changing the language refreshes every binding and switches the flow direction (RTL for Persian).
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    private static readonly ResourceManager Resources =
        new("SDM.App.Resources.Strings", typeof(Loc).Assembly);

    public static Loc Instance { get; } = new();

    public static readonly IReadOnlyList<(string Code, string Name)> Languages = new[]
    {
        ("fa", "فارسی"),
        ("en", "English"),
        ("de", "Deutsch"),
    };

    private CultureInfo _culture = CultureInfo.GetCultureInfo("fa");

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key] => Resources.GetString(key, _culture) ?? key;

    public static string Get(string key) => Instance[key];

    public CultureInfo Culture => _culture;

    public FlowDirection FlowDirection =>
        _culture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public void SetLanguage(string code)
    {
        _culture = CultureInfo.GetCultureInfo(string.IsNullOrWhiteSpace(code) ? "fa" : code);
        CultureInfo.CurrentUICulture = _culture;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FlowDirection)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
    }
}
