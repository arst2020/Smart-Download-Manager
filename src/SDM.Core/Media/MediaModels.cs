namespace SDM.Core.Media;

public enum MediaKind
{
    Unknown,
    Series,
    Movie,
    Music,
    Software,
    Document,
    Archive,
    Image,
    Other
}

/// <summary>What a download is, as far as the local parsers, the AI and TMDB can tell.</summary>
public sealed record MediaInfo
{
    public MediaKind Kind { get; init; } = MediaKind.Unknown;
    public string? Title { get; init; }
    public int? Year { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public string? EpisodeTitle { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public int? TmdbId { get; init; }
    public string? PosterUrl { get; init; }
    public double Confidence { get; init; }

    /// <summary>Which steps produced this, e.g. "local", "local + TMDB", "Claude + TMDB".</summary>
    public string Source { get; init; } = "";

    /// <summary>Short reason from the AI, shown in the suggestion popup.</summary>
    public string? Reason { get; init; }

    public bool IsVideo => Kind is MediaKind.Series or MediaKind.Movie;
}

/// <summary>A library of a media server (Emby / Jellyfin), e.g. "TV Shows" → E:\Media\TV.</summary>
public sealed record MediaLibrary(string Name, string CollectionType, IReadOnlyList<string> Locations, string Server)
{
    public MediaKind Kind => CollectionType.ToLowerInvariant() switch
    {
        "tvshows" => MediaKind.Series,
        "movies" => MediaKind.Movie,
        "music" => MediaKind.Music,
        _ => MediaKind.Unknown // mixed, homevideos, books ...
    };
}

/// <summary>A place the popup offers to save the file.</summary>
public sealed record FolderOption(string Label, string Folder);

/// <summary>The suggestion shown to the user before (or instead of) asking.</summary>
public sealed record SaveSuggestion
{
    public required MediaInfo Media { get; init; }

    /// <summary>Recommended folder (absolute, or relative to the download directory).</summary>
    public required string Folder { get; init; }

    /// <summary>Original file name from the server.</summary>
    public required string OriginalFileName { get; init; }

    /// <summary>Standard name for media servers, e.g. "Dark - S01E03.mkv"; null when not applicable.</summary>
    public string? StandardFileName { get; init; }

    /// <summary>The media server library the folder belongs to, if any.</summary>
    public MediaLibrary? Library { get; init; }

    /// <summary>Other sensible destinations (other libraries, the type folder, the download folder).</summary>
    public IReadOnlyList<FolderOption> Alternatives { get; init; } = Array.Empty<FolderOption>();

    /// <summary>A user rule decided the folder.</summary>
    public string? RuleName { get; init; }

    /// <summary>Human-readable summary, e.g. "Dark (2017) · S01E03".</summary>
    public string Describe()
    {
        var m = Media;
        string title = m.Title ?? OriginalFileName;
        string year = m.Year is int y ? $" ({y})" : "";
        return m.Kind switch
        {
            MediaKind.Series when m.Season is int s && m.Episode is int e => $"{title}{year} · S{s:00}E{e:00}",
            MediaKind.Music when m.Artist is not null => $"{m.Artist} – {m.Album ?? title}",
            _ => title + year
        };
    }
}
