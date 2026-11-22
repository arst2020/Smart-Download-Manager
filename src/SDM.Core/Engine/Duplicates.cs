using System.Text;
using SDM.Core.Media;
using SDM.Core.Models;

namespace SDM.Core.Engine;

public enum DuplicateKind
{
    /// <summary>The same download is already in the list and not finished (waiting, running, paused, failed).</summary>
    InProgress,

    /// <summary>It was downloaded before and the file is still there.</summary>
    Downloaded,

    /// <summary>A file with this name is already in the destination folder.</summary>
    FileExists,
}

/// <summary>What to do with a duplicate (the setting; Ask shows the window).</summary>
public enum DuplicateAction { Ask, OpenExisting, NewName, Replace, Skip }

/// <summary>Why the new download looks like one we already have.</summary>
public enum DuplicateReason { SameLink, SameName, SameEpisode, SameMovie }

public sealed record DuplicateMatch(DuplicateKind Kind, DuplicateReason Reason, string? ExistingPath, long? ExistingSize,
    DateTime? ExistingDate, DownloadJob? Job);

/// <summary>What is known about a download before it is added.</summary>
public sealed record DuplicateQuery
{
    public required Uri Url { get; init; }

    /// <summary>Name on the server ("Chicago.Fire.S02E02.480p.BluRay.mkv").</summary>
    public string? OriginalFileName { get; init; }

    /// <summary>Name it will be saved as (e.g. the standard "Chicago Fire - S02E02.mkv").</summary>
    public string? TargetFileName { get; init; }

    /// <summary>Destination folder (absolute or relative to the download directory); null if not decided yet.</summary>
    public string? Folder { get; init; }

    /// <summary>Same episode / movie regardless of quality or naming (see <see cref="Duplicates.MediaKey"/>).</summary>
    public string? MediaKey { get; init; }
}

public static class Duplicates
{
    /// <summary>
    /// "series|chicagofire|2|2" or "movie|dunepart two|2024": the same episode or movie, whatever the quality or file name.
    /// Null when the title is unknown.
    /// </summary>
    public static string? MediaKey(MediaInfo media) => media switch
    {
        { Kind: MediaKind.Series, Title: { Length: > 0 } t, Episode: int e } => $"series|{Squash(t)}|{media.Season ?? 1}|{e}",
        { Kind: MediaKind.Movie, Title: { Length: > 0 } t } => $"movie|{Squash(t)}|{media.Year?.ToString() ?? ""}",
        _ => null
    };

    private static string Squash(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormKC))
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Link without the "#…" part; host compared case-insensitively.</summary>
    internal static string LinkKey(Uri url) =>
        url.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped).ToLowerInvariant()
        + url.GetComponents(UriComponents.PathAndQuery, UriFormat.Unescaped);
}
