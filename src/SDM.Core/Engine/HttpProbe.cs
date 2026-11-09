using System.Net;
using System.Net.Http.Headers;
using SDM.Core.Models;
using SDM.Core.Util;

namespace SDM.Core.Engine;

public static class HttpProbe
{
    /// <summary>
    /// Sends GET with "Range: bytes=0-0" and reads only the headers:
    /// size, range support, file name, MIME type and validators (ETag / Last-Modified).
    /// </summary>
    public static async Task<ProbeResult> ProbeAsync(
        HttpClient http, Uri url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        HttpHeaderHelper.Apply(request, headers);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            // Some servers reject "Range: bytes=0-0" (416 for empty files, 403/503 from picky CDNs):
            // ask again the normal way and only read the headers.
            return await ProbeWithoutRangeAsync(http, url, headers, ct).ConfigureAwait(false);
        }

        ThrowIfFailed(response);
        return Build(response, url);
    }

    private static void ThrowIfFailed(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new HttpRequestException(
            $"The download server answered {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
    }

    private static async Task<ProbeResult> ProbeWithoutRangeAsync(
        HttpClient http, Uri url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            HttpHeaderHelper.Apply(request, headers);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            bool busy = response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests;
            if (busy && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), ct).ConfigureAwait(false); // server busy: wait a moment
                continue;
            }
            ThrowIfFailed(response);
            return Build(response, url);
        }
    }

    /// <summary>What can be known without asking the server (used when the probe fails).</summary>
    public static ProbeResult FromUrlOnly(Uri url) =>
        new(url, null, false, FileNameHelper.Resolve(null, url, null), null, null, null);

    private static ProbeResult Build(HttpResponseMessage response, Uri requested)
    {
        var finalUri = response.RequestMessage?.RequestUri ?? requested;
        var content = response.Content.Headers;

        long? length;
        bool acceptRanges;
        if (response.StatusCode == HttpStatusCode.PartialContent && content.ContentRange is { HasLength: true } cr)
        {
            length = cr.Length;
            acceptRanges = true;
        }
        else
        {
            // Server ignored the Range header: single connection, no resume.
            length = content.ContentLength;
            acceptRanges = false;
        }

        string? contentType = content.ContentType?.MediaType;
        string fileName = FileNameHelper.Resolve(content.ContentDisposition, finalUri, contentType);
        string? etag = response.Headers.ETag?.ToString();

        return new ProbeResult(finalUri, length, acceptRanges, fileName, contentType, etag, content.LastModified);
    }
}

internal static class HttpHeaderHelper
{
    public static void Apply(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return;
        foreach (var (key, value) in headers)
        {
            if (!request.Headers.TryAddWithoutValidation(key, value))
                request.Content?.Headers.TryAddWithoutValidation(key, value);
        }
    }
}
