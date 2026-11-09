using System.Net;
using System.Text;

namespace SDM.Core.Tests;

/// <summary>HttpMessageHandler that answers from a function and records every request.</summary>
public sealed class FakeHttp : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _respond;

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = new();

    public FakeHttp(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) => _respond = respond;

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public HttpClient Client() => new(this);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return _respond(request, body);
    }
}
