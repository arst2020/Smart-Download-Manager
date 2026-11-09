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

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Typically an empty file: retry without Range to get headers.
            return await ProbeWithoutRangeAsync(http, url, headers, ct).ConfigureAwait(false);
        }

        response.EnsureSuccessStatusCode();
        return Build(response, url);
    }

    private static async Task<ProbeResult> ProbeWithoutRangeAsync(
        HttpClient http, Uri url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        HttpHeaderHelper.Apply(request, headers);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return Build(response, url);
    }

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
