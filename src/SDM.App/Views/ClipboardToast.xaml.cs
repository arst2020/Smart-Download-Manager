using System.Windows;
using System.Windows.Threading;
using SDM.App.Localization;

namespace SDM.App.Views;

/// <summary>
/// Small popup in the corner of the screen after a download link was copied in another program.
/// It does not take focus and closes by itself after a few seconds (the timer pauses while the mouse is over it).
/// </summary>
public partial class ClipboardToast : Window
{
    private static ClipboardToast? _current;
    private readonly IReadOnlyList<Uri> _links;
    private readonly Action<IReadOnlyList<Uri>> _download;
    private readonly DispatcherTimer _timer;

    private ClipboardToast(IReadOnlyList<Uri> links, Action<IReadOnlyList<Uri>> download)
    {
        InitializeComponent();
        _links = links;
        _download = download;
        TitleText.Text = links.Count == 1 ? Loc.Get("ClipboardOne") : string.Format(Loc.Get("ClipboardMany"), links.Count);
        LinkText.Text = links.Count == 1 ? links[0].ToString() : string.Join("  ·  ", links.Take(3).Select(FileLabel));
        LinkText.ToolTip = string.Join("\n", links.Take(20));
        DownloadText.Text = links.Count == 1 ? Loc.Get("ClipDownload") : Loc.Get("ClipBatch");

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(9) };
        _timer.Tick += (_, _) => Close();
        Loaded += (_, _) => { PlaceInCorner(); _timer.Start(); };
        Closed += (_, _) => { _timer.Stop(); if (_current == this) _current = null; };
    }

    /// <summary>Shows the popup (replacing one that is still open).</summary>
    public static void ShowFor(IReadOnlyList<Uri> links, Action<IReadOnlyList<Uri>> download)
    {
        _current?.Close();
        _current = new ClipboardToast(links, download);
        _current.Show();
    }

    private static string FileLabel(Uri u)
    {
        string name = Uri.UnescapeDataString(u.Segments.LastOrDefault() ?? u.Host);
        return string.IsNullOrWhiteSpace(name) || name == "/" ? u.Host : name;
    }

    /// <summary>Bottom corner of the work area (above the taskbar): right for LTR languages, left for RTL.</summary>
    private void PlaceInCorner()
    {
        var area = SystemParameters.WorkArea;
        Top = area.Bottom - ActualHeight - 4;
        Left = FlowDirection == FlowDirection.RightToLeft ? area.Left + 4 : area.Right - ActualWidth - 4;
    }

    private void OnDownload(object sender, RoutedEventArgs e)
    {
        Close();
        _download(_links);
    }

    private void OnIgnore(object sender, RoutedEventArgs e) => Close();

    private void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => _timer.Stop();

    private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => _timer.Start();
}
