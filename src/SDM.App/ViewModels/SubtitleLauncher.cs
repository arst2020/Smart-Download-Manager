using System.Diagnostics;
using System.IO;
using SDM.Core.Engine;
using SDM.Core.Media;
using SDM.Core.Settings;

namespace SDM.App.ViewModels;

/// <summary>
/// "Search subtitles": opens the subtitle site's search page in the default browser.
/// Nothing is downloaded by the app — the user picks a subtitle on the site themselves.
/// </summary>
public static class SubtitleLauncher
{
    public static bool CanSearch(DownloadJob? job) => job is not null && SubtitleSearch.IsVideo(NameOf(job));

    private static string NameOf(DownloadJob job) => Path.GetFileName(
        job.FilePath ?? job.FileName ?? job.Request.FileName ?? Uri.UnescapeDataString(job.Request.Url.AbsolutePath));

    public static bool Open(AppSettings settings, DownloadJob job)
    {
        Uri? url = SubtitleSearch.BuildUrl(settings.SubtitleSearchUrl, SubtitleSearch.BuildQuery(NameOf(job)));
        if (url is null) return false;
        try
        {
            Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false; // no default browser
        }
    }
}
