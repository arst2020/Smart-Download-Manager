using System.IO;
using System.Windows;
using SDM.App.Localization;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace SDM.App;

/// <summary>
/// The icon in the Windows notification area: double-click opens the window, right-click shows
/// Open / Add link / Pause all / Exit. Its balloon tips appear as Windows notifications.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _open, _add, _pauseAll, _exit;
    private Action? _balloonClick;

    public event Action? OpenRequested;
    public event Action? AddRequested;
    public event Action? PauseAllRequested;
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _open = new Forms.ToolStripMenuItem("", null, (_, _) => OpenRequested?.Invoke());
        _add = new Forms.ToolStripMenuItem("", null, (_, _) => AddRequested?.Invoke());
        _pauseAll = new Forms.ToolStripMenuItem("", null, (_, _) => PauseAllRequested?.Invoke());
        _exit = new Forms.ToolStripMenuItem("", null, (_, _) => ExitRequested?.Invoke());
        _open.Font = new Drawing.Font(_open.Font, Drawing.FontStyle.Bold);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.AddRange(new Forms.ToolStripItem[] { _open, _add, _pauseAll, new Forms.ToolStripSeparator(), _exit });
        menu.Opening += (_, _) => ApplyTexts();

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => { _balloonClick?.Invoke(); _balloonClick = null; };
        _icon.BalloonTipClosed += (_, _) => _balloonClick = null;
        ApplyTexts();
    }

    /// <summary>Re-reads the menu texts (the language can change while the app runs).</summary>
    public void ApplyTexts()
    {
        _icon.Text = Truncate(Loc.Get("AppTitle"), 63);
        _open.Text = Loc.Get("TrayOpen");
        _add.Text = Loc.Get("AddLink");
        _pauseAll.Text = Loc.Get("PauseAll");
        _exit.Text = Loc.Get("TrayExit");
        var rtl = Loc.Instance.FlowDirection == FlowDirection.RightToLeft;
        _icon.ContextMenuStrip!.RightToLeft = rtl ? Forms.RightToLeft.Yes : Forms.RightToLeft.No;
    }

    /// <summary>Tooltip on hover, e.g. "Smart Download Manager — 2 active · 3.4 MB/s" (Windows allows 63 characters).</summary>
    public void SetStatus(string? status) =>
        _icon.Text = Truncate(string.IsNullOrEmpty(status) ? Loc.Get("AppTitle") : $"{Loc.Get("AppTitle")} — {status}", 63);

    /// <summary>Shows a Windows notification; <paramref name="onClick"/> runs if the user clicks it.</summary>
    public void Notify(string title, string text, bool error = false, Action? onClick = null)
    {
        _balloonClick = onClick;
        _icon.ShowBalloonTip(5000, Truncate(title, 63), Truncate(string.IsNullOrWhiteSpace(text) ? " " : text, 255),
            error ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Info);
    }

    private static Drawing.Icon LoadIcon()
    {
        using Stream? stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("SDM.App.app.ico");
        if (stream is not null) return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        return Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? Drawing.SystemIcons.Application;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        _icon.Visible = false; // otherwise a ghost icon stays until the mouse passes over it
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
