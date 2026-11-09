using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SDM.Core.Util;

namespace SDM.App;

/// <summary>
/// Listens for clipboard changes (WM_CLIPBOARDUPDATE) and reports copied download links.
/// Page links, the app's own copies and the same text copied twice are ignored.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private readonly HwndSource _source;
    private readonly DispatcherTimer _debounce;
    private string _lastText = "";
    private static string? _ownCopy;

    /// <summary>Raised on the UI thread with the downloadable links found.</summary>
    public event Action<IReadOnlyList<Uri>>? LinksCopied;

    public bool Enabled { get; set; } = true;

    /// <param name="window">Any window that lives as long as the app (hiding it is fine; its handle stays).</param>
    public ClipboardWatcher(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("No window handle");
        _source.AddHook(WndProc);
        AddClipboardFormatListener(handle);

        // Programs often write the clipboard several times in a row: read once it has settled.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); Check(); };

        // Whatever is on the clipboard when the app starts is old news.
        _lastText = ReadText() ?? "";
    }

    /// <summary>Call before the app itself copies text ("Copy link"), so it does not offer it back.</summary>
    public static void IgnoreNext(string text) => _ownCopy = text;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE && Enabled)
        {
            _debounce.Stop();
            _debounce.Start();
        }
        return IntPtr.Zero;
    }

    private void Check()
    {
        string? text = ReadText();
        if (string.IsNullOrWhiteSpace(text) || text == _lastText) return;
        _lastText = text;
        if (text == _ownCopy) { _ownCopy = null; return; }
        if (text.Length > 100_000) return; // a pasted document, not a link

        var links = LinkExtractor.ExtractDownloadable(text);
        if (links.Count > 0) LinksCopied?.Invoke(links);
    }

    private static string? ReadText()
    {
        // The program that just copied may still hold the clipboard open.
        for (int i = 0; i < 3; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (COMException) { Thread.Sleep(40); }
            catch (ExternalException) { Thread.Sleep(40); }
        }
        return null;
    }

    public void Dispose()
    {
        _debounce.Stop();
        RemoveClipboardFormatListener(_source.Handle);
        _source.RemoveHook(WndProc);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
