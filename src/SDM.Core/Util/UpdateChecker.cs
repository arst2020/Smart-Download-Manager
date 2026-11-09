using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace SDM.Core.Util;

/// <summary>A newer release on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Tag, Uri PageUrl, Uri? InstallerUrl, Uri? PortableUrl, string? Notes)
{
    /// <summary>SHA256SUMS.txt of the release; the installer is only run if its checksum is listed there and matches.</summary>
    public Uri? ChecksumsUrl { get; init; }
}

/// <summary>
/// Looks at the project's latest GitHub release. Only the public release list is read; nothing about the
/// user or the PC is sent (GitHub sees an ordinary request with the app's name as User-Agent).
/// </summary>
public sealed class UpdateChecker
{
    public const string Repository = "arst2020/Smart-Download-Manager";

    private readonly HttpClient _http;
    private readonly Uri _endpoint;

    public UpdateChecker(HttpClient http, Uri? endpoint = null)
    {
        _http = http;
        _endpoint = endpoint ?? new Uri($"https://api.github.com/repos/{Repository}/releases/latest");
    }

    public static Version CurrentVersion =>
        (Assembly.GetEntryAssembly() ?? typeof(UpdateChecker).Assembly).GetName().Version is { } v
            ? new Version(v.Major, v.Minor, Math.Max(0, v.Build))
            : new Version(0, 0, 0);

    /// <summary>The latest release if it is newer than <paramref name="current"/>; null when up to date or no release exists.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version? current = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _endpoint);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("SmartDownloadManager", CurrentVersion.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // no release yet
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return Parse(doc.RootElement, current ?? CurrentVersion);
    }

    public static UpdateInfo? Parse(JsonElement release, Version current)
    {
        if (release.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (release.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!TryParseVersion(tag, out var version) || version <= current) return null;

        Uri? installer = null, portable = null, sums = null;
        if (release.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url) || !IsTrusted(url))
                    continue;
                if (name.EndsWith("-Setup.exe", StringComparison.OrdinalIgnoreCase)) installer = url;
                else if (name.EndsWith("-Portable.zip", StringComparison.OrdinalIgnoreCase)) portable = url;
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) sums = url;
            }
        }
        var page = Uri.TryCreate(release.GetProperty("html_url").GetString(), UriKind.Absolute, out var html) && IsTrusted(html)
            ? html
            : new Uri($"https://github.com/{Repository}/releases/latest");
        string? notes = release.TryGetProperty("body", out var b) ? b.GetString() : null;
        return new UpdateInfo(version, tag, page, installer, portable, notes) { ChecksumsUrl = sums };
    }

    /// <summary>
    /// Downloads the installer into <paramref name="folder"/> and checks it against SHA256SUMS.txt from the same
    /// release. Throws if the checksum list is missing or does not match, so a damaged or swapped file never runs.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(UpdateInfo info, string folder, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (info.InstallerUrl is null || !IsTrusted(info.InstallerUrl)) throw new InvalidOperationException("No installer in this release");
        if (info.ChecksumsUrl is null || !IsTrusted(info.ChecksumsUrl)) throw new InvalidOperationException("The release has no SHA256SUMS.txt");

        string name = Path.GetFileName(Uri.UnescapeDataString(info.InstallerUrl.AbsolutePath));
        string sums = await _http.GetStringAsync(info.ChecksumsUrl, ct).ConfigureAwait(false);
        string? expected = sums.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            .Select(HashVerifier.Normalize)
            .FirstOrDefault(h => h?.Length == 64);
        if (expected is null) throw new InvalidOperationException($"{name} is not listed in SHA256SUMS.txt");

        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        using (var response = await _http.GetAsync(info.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = File.Create(path);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(done / (double)total.Value);
            }
        }
        string actual = await HashVerifier.ComputeAsync(path, HashAlgorithmKind.Sha256, null, ct).ConfigureAwait(false);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(path);
            throw new InvalidOperationException("The downloaded installer does not match its published checksum");
        }
        return path;
    }

    /// <summary>Only files from this project's GitHub releases are accepted.</summary>
    public static bool IsTrusted(Uri url) =>
        url.Scheme == Uri.UriSchemeHttps && url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && url.AbsolutePath.StartsWith($"/{Repository}/", StringComparison.OrdinalIgnoreCase);

    /// <summary>"v1.2.3", "1.2", "v0.4.0-beta" → version (suffix ignored).</summary>
    public static bool TryParseVersion(string tag, out Version version)
    {
        string t = tag.Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) t = t[..dash];
        if (Version.TryParse(t.Contains('.') ? t : t + ".0", out var v))
        {
            version = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }
}
