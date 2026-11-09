using SDM.Core.Classification;
using SDM.Core.Media.Ai;
using SDM.Core.Settings;

namespace SDM.Core.Media;

public sealed record SuggestRequest(string FileName, Uri? Url = null, string? ContentType = null, long? Size = null,
    string? PageTitle = null);

/// <summary>
/// Decides where a download should go and what it should be called:
///   1. local parser (free, offline)  2. AI when the parser is unsure  3. TMDB for the official title/year
///   4. user rules win over everything  5. media server library for the kind  6. fallback folders.
/// Every network step is optional and failures fall back silently, so a suggestion is always returned.
/// </summary>
public sealed class MediaAdvisor
{
    private static readonly TimeSpan LibraryCacheTime = TimeSpan.FromMinutes(5);

    private readonly Func<AppSettings> _settings;
    private readonly HttpClient _http;
    private readonly Func<AiSettings, ILlmClient?> _llmFactory;
    private IReadOnlyList<MediaLibrary> _libraries = Array.Empty<MediaLibrary>();
    private DateTime _librariesAt = DateTime.MinValue;
    private string _librariesKey = "";

    public MediaAdvisor(Func<AppSettings> settings, HttpClient http, Func<AiSettings, ILlmClient?>? llmFactory = null)
    {
        _settings = settings;
        _http = http;
        _llmFactory = llmFactory ?? (s => LlmClientFactory.Create(s, http));
    }

    /// <summary>Last error from an optional service (AI/TMDB/server), shown as a hint in the UI.</summary>
    public string? LastWarning { get; private set; }

    public async Task<SaveSuggestion> SuggestAsync(SuggestRequest req, CancellationToken ct = default)
    {
        var settings = _settings();
        LastWarning = null;

        // 1-3: identify
        MediaInfo media = MediaNameParser.Parse(req.FileName, req.ContentType);
        media = await AskAiIfNeededAsync(media, req, settings, ct).ConfigureAwait(false);
        media = await EnrichWithTmdbAsync(media, settings, ct).ConfigureAwait(false);

        // 5: libraries (cached)
        var libraries = await GetLibrariesAsync(settings, ct).ConfigureAwait(false);
        // Only libraries whose folder exists on this PC (Emby may run on a NAS with its own paths).
        MediaLibrary? library = libraries.FirstOrDefault(l =>
            l.Kind == media.Kind && media.Kind != MediaKind.Unknown && LocalRoot(l) is not null);

        if (library is null && settings.MediaServer.IsConfigured && media.Kind is MediaKind.Series or MediaKind.Movie or MediaKind.Music
            && LastWarning is null)
        {
            // Explain why the server's library was not used, instead of silently falling back.
            var sameKind = libraries.Where(l => l.Kind == media.Kind).ToList();
            LastWarning = sameKind.Count > 0
                ? $"{settings.MediaServer.Type}: {sameKind[0].Name} → {string.Join(", ", sameKind[0].Locations)} — folder not found on this PC"
                : $"{settings.MediaServer.Type}: no {media.Kind} library ({(libraries.Count == 0 ? "no libraries returned" : string.Join(", ", libraries.Select(l => $"{l.Name} [{l.CollectionType}]")))})";
        }

        string folder = FolderFor(media, settings, library, req.FileName, req.ContentType);
        string? ruleName = null;

        // 4: user rules
        var ruleContext = new ClassificationContext(req.FileName, req.Url, req.ContentType,
            string.Join(' ', new[] { req.PageTitle, media.Title }.Where(s => !string.IsNullOrWhiteSpace(s))));
        var ruleResult = await new UserRuleClassifier(() => settings.Rules).ClassifyAsync(ruleContext, ct).ConfigureAwait(false);
        if (ruleResult is not null)
        {
            folder = ruleResult.Folder;
            ruleName = ruleResult.Category;
            library = libraries.FirstOrDefault(l => l.Locations.Any(loc => IsUnder(folder, loc)));
        }

        return new SaveSuggestion
        {
            Media = media,
            Folder = folder,
            OriginalFileName = req.FileName,
            StandardFileName = MediaNamer.StandardFileName(media, req.FileName),
            Library = library,
            RuleName = ruleName,
            Alternatives = BuildAlternatives(media, settings, libraries, folder),
        };
    }

    private async Task<MediaInfo> AskAiIfNeededAsync(MediaInfo local, SuggestRequest req, AppSettings settings, CancellationToken ct)
    {
        bool unsure = local.Confidence < ClassifierPipeline.ConfidenceThreshold;
        if (!unsure && !settings.AiAlways) return local;

        var client = _llmFactory(settings.Ai);
        if (client is null) return local;

        try
        {
            var ai = await new LlmMediaDetector(client)
                .DetectAsync(req.FileName, req.Url, req.ContentType, req.Size, req.PageTitle, ct).ConfigureAwait(false);
            if (ai is null || ai.Kind == MediaKind.Unknown) return local;

            // Local season/episode numbers come straight from the file name, so they win if present.
            if (local.Kind == MediaKind.Series && ai.Kind == MediaKind.Series && !unsure)
                ai = ai with { Season = local.Season ?? ai.Season, Episode = local.Episode ?? ai.Episode };

            return ai.Confidence >= local.Confidence || unsure ? ai : local;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            LastWarning = $"{client.DisplayName}: {ex.Message}";
            return local;
        }
    }

    private async Task<MediaInfo> EnrichWithTmdbAsync(MediaInfo media, AppSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.TmdbApiKey) || !media.IsVideo || string.IsNullOrWhiteSpace(media.Title))
            return media;
        try
        {
            var tmdb = new TmdbClient(_http, settings.TmdbApiKey.Trim(), settings.TmdbLanguage);
            if (media.Kind == MediaKind.Series)
            {
                var match = await tmdb.FindSeriesAsync(media.Title, media.Year, ct).ConfigureAwait(false);
                if (match is null) return media;
                string? epTitle = media.Episode is int e
                    ? await tmdb.GetEpisodeTitleAsync(match.Id, media.Season ?? 1, e, ct).ConfigureAwait(false)
                    : null;
                return media with
                {
                    Title = match.Title, Year = match.Year ?? media.Year, TmdbId = match.Id, PosterUrl = match.PosterUrl,
                    EpisodeTitle = epTitle, Confidence = Math.Max(media.Confidence, 0.9), Source = media.Source + " + TMDB"
                };
            }
            else
            {
                var match = await tmdb.FindMovieAsync(media.Title, media.Year, ct).ConfigureAwait(false);
                if (match is null) return media;
                return media with
                {
                    Title = match.Title, Year = match.Year ?? media.Year, TmdbId = match.Id, PosterUrl = match.PosterUrl,
                    Confidence = Math.Max(media.Confidence, 0.9), Source = media.Source + " + TMDB"
                };
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            LastWarning = $"TMDB: {ex.Message}";
            return media;
        }
    }

    public async Task<IReadOnlyList<MediaLibrary>> GetLibrariesAsync(AppSettings settings, CancellationToken ct, bool force = false)
    {
        var ms = settings.MediaServer;
        if (!ms.IsConfigured) return Array.Empty<MediaLibrary>();

        string key = $"{ms.Type}|{ms.Url}|{ms.ApiKey}";
        if (!force && key == _librariesKey && DateTime.UtcNow - _librariesAt < LibraryCacheTime) return _libraries;
        try
        {
            _libraries = await new MediaServerClient(_http, ms).GetLibrariesAsync(ct).ConfigureAwait(false);
            _librariesAt = DateTime.UtcNow;
            _librariesKey = key;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            LastWarning = $"{ms.Type}: {ex.Message}";
        }
        return _libraries;
    }

    /// <summary>After a file landed in a library folder, ask the media server to scan.</summary>
    public async Task NotifyFileSavedAsync(string filePath, CancellationToken ct = default)
    {
        var settings = _settings();
        if (!settings.MediaServer.IsConfigured || !settings.MediaServer.RefreshAfterDownload) return;
        var libraries = await GetLibrariesAsync(settings, ct).ConfigureAwait(false);
        if (!libraries.Any(l => l.Locations.Any(loc => IsUnder(filePath, loc)))) return;
        try { await new MediaServerClient(_http, settings.MediaServer).RefreshAsync(ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { LastWarning = $"{settings.MediaServer.Type}: {ex.Message}"; }
    }

    private static string FolderFor(MediaInfo m, AppSettings s, MediaLibrary? library, string fileName, string? contentType)
    {
        string? libRoot = library is null ? null : LocalRoot(library);
        return m.Kind switch
        {
            MediaKind.Series => MediaNamer.SeriesFolder(libRoot ?? s.EffectiveSeriesDirectory, m),
            MediaKind.Movie => MediaNamer.MovieFolder(libRoot ?? s.EffectiveMoviesDirectory, m),
            MediaKind.Music => MediaNamer.MusicFolder(libRoot ?? Path.Combine(s.DownloadDirectory, s.FolderForCategory("Music")), m),
            MediaKind.Document => s.FolderForCategory("Documents"),
            MediaKind.Archive => s.FolderForCategory("Compressed"),
            MediaKind.Software => s.FolderForCategory("Programs"),
            MediaKind.Image => s.FolderForCategory("Images"),
            _ => s.FolderForCategory(TypeCategory(fileName, contentType) ?? "Other"),
        };
    }

    private static string? TypeCategory(string fileName, string? contentType)
    {
        string ext = Path.GetExtension(fileName);
        foreach (var (category, exts) in TypeClassifier.DefaultExtensions)
            if (exts.Contains(ext, StringComparer.OrdinalIgnoreCase)) return category;
        return TypeClassifier.FromMime(contentType);
    }

    private static IReadOnlyList<FolderOption> BuildAlternatives(MediaInfo m, AppSettings s,
        IReadOnlyList<MediaLibrary> libraries, string chosen)
    {
        var options = new List<FolderOption>();
        foreach (var lib in libraries)
        {
            if (LocalRoot(lib) is not { } root) continue;
            string folder = lib.Kind switch
            {
                MediaKind.Series when m.Kind == MediaKind.Series => MediaNamer.SeriesFolder(root, m),
                MediaKind.Movie when m.Kind == MediaKind.Movie => MediaNamer.MovieFolder(root, m),
                _ => root
            };
            options.Add(new FolderOption($"{lib.Server}: {lib.Name}", folder));
        }
        if (m.Kind == MediaKind.Series) options.Add(new FolderOption("Series", MediaNamer.SeriesFolder(s.EffectiveSeriesDirectory, m)));
        if (m.Kind == MediaKind.Movie) options.Add(new FolderOption("Movies", MediaNamer.MovieFolder(s.EffectiveMoviesDirectory, m)));
        options.Add(new FolderOption("Downloads", s.DownloadDirectory));

        string full(string f) => Path.GetFullPath(Path.Combine(s.DownloadDirectory, f));
        return options
            .GroupBy(o => full(o.Folder), OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .Select(g => g.First())
            .Where(o => full(o.Folder) != full(chosen))
            .ToList();
    }

    /// <summary>First library folder that exists on this computer.</summary>
    internal static string? LocalRoot(MediaLibrary lib) => lib.Locations.FirstOrDefault(Directory.Exists);

    internal static bool IsUnder(string path, string root)
    {
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(r, cmp);
    }
}
