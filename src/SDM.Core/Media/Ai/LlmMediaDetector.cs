using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SDM.Core.Media.Ai;

/// <summary>Asks a language model what a download is. Only metadata is sent — never file contents.</summary>
public sealed class LlmMediaDetector(ILlmClient client)
{
    internal const string SystemPrompt = """
        You identify what a file being downloaded is, from its metadata only.
        Reply with ONE JSON object and nothing else:
        {"kind":"series|movie|music|software|document|archive|image|other",
         "title":string|null, "year":number|null, "season":number|null, "episode":number|null,
         "artist":string|null, "album":string|null, "confidence":number, "reason":string}
        Rules:
        - "title" is the clean official title of the show, movie, album or program, without quality,
          codec, release-group or language tags (1080p, x265, WEB-DL, [Group], Dubbed, ...).
          Keep non-Latin titles (e.g. Persian) in their original script.
        - For series give season and episode numbers; if only an episode number is known, season is 1.
        - "confidence" is 0..1. Use below 0.5 when you are guessing.
        - "reason" is one short sentence in English.
        """;

    public string DisplayName => client.DisplayName;

    public async Task<MediaInfo?> DetectAsync(string fileName, Uri? url, string? contentType, long? size,
        string? pageTitle, CancellationToken ct)
    {
        var user = new StringBuilder();
        user.AppendLine($"File name: {fileName}");
        if (url is not null) user.AppendLine($"URL: {url}");
        if (!string.IsNullOrEmpty(contentType)) user.AppendLine($"Content-Type: {contentType}");
        if (size is long s) user.AppendLine($"Size: {s / (1024.0 * 1024):0.#} MB");
        if (!string.IsNullOrEmpty(pageTitle)) user.AppendLine($"Page title: {pageTitle}");

        string answer = await client.CompleteAsync(SystemPrompt, user.ToString(), ct).ConfigureAwait(false);
        var info = Parse(answer);
        return info is null ? null : info with { Source = client.DisplayName };
    }

    /// <summary>Parses the model's answer; tolerant of code fences and text around the JSON.</summary>
    public static MediaInfo? Parse(string answer)
    {
        int start = answer.IndexOf('{');
        int end = answer.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        JsonNode? node;
        try { node = JsonNode.Parse(answer[start..(end + 1)]); }
        catch (JsonException) { return null; }
        if (node is not JsonObject o) return null;

        var kind = (Str(o, "kind") ?? "").ToLowerInvariant() switch
        {
            "series" or "tv" or "tvshow" or "episode" => MediaKind.Series,
            "movie" or "film" => MediaKind.Movie,
            "music" or "audio" or "album" => MediaKind.Music,
            "software" or "program" or "app" => MediaKind.Software,
            "document" or "book" or "ebook" => MediaKind.Document,
            "archive" => MediaKind.Archive,
            "image" => MediaKind.Image,
            "other" => MediaKind.Other,
            _ => MediaKind.Unknown
        };

        int? season = Int(o, "season"), episode = Int(o, "episode");
        if (kind == MediaKind.Series && episode is not null && season is null) season = 1;

        return new MediaInfo
        {
            Kind = kind,
            Title = Str(o, "title"),
            Year = Int(o, "year"),
            Season = season,
            Episode = episode,
            Artist = Str(o, "artist"),
            Album = Str(o, "album"),
            Confidence = Math.Clamp(Dbl(o, "confidence") ?? 0.5, 0, 1),
            Reason = Str(o, "reason"),
        };
    }

    private static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;

    private static int? Int(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)d;
        if (v.TryGetValue<string>(out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out i)) return i;
        return null;
    }

    private static double? Dbl(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        return null;
    }
}
