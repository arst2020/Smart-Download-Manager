using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SDM.Core.Media.Ai;

public enum AiProvider
{
    None,
    Claude,
    OpenAI,
    Gemini,
    Ollama,
    /// <summary>Any server with an OpenAI-style /chat/completions endpoint (LM Studio, OpenRouter, ...).</summary>
    OpenAICompatible
}

public sealed class AiSettings
{
    public AiProvider Provider { get; set; } = AiProvider.None;
    public string ApiKey { get; set; } = "";

    /// <summary>Empty = the provider's default model (see <see cref="LlmClientFactory.DefaultModel"/>).</summary>
    public string Model { get; set; } = "";

    /// <summary>Empty = the provider's default endpoint.</summary>
    public string BaseUrl { get; set; } = "";
}

/// <summary>Sends one prompt and returns the model's text answer.</summary>
public interface ILlmClient
{
    string DisplayName { get; }
    Task<string> CompleteAsync(string system, string user, CancellationToken ct);
}

public static class LlmClientFactory
{
    public static string DefaultModel(AiProvider p) => p switch
    {
        AiProvider.Claude => "claude-haiku-5-5",
        AiProvider.OpenAI => "gpt-4o-mini",
        AiProvider.Gemini => "gemini-2.0-flash",
        AiProvider.Ollama => "llama3.2",
        _ => ""
    };

    public static string DefaultBaseUrl(AiProvider p) => p switch
    {
        AiProvider.Claude => "https://api.anthropic.com",
        AiProvider.OpenAI => "https://api.openai.com/v1",
        AiProvider.Gemini => "https://generativelanguage.googleapis.com/v1beta/openai",
        AiProvider.Ollama => "http://localhost:11434",
        AiProvider.OpenAICompatible => "http://localhost:1234/v1",
        _ => ""
    };

    public static bool NeedsApiKey(AiProvider p) => p is AiProvider.Claude or AiProvider.OpenAI or AiProvider.Gemini;

    /// <summary>Returns null when AI is off or not configured.</summary>
    public static ILlmClient? Create(AiSettings s, HttpClient http)
    {
        if (s.Provider == AiProvider.None) return null;
        if (NeedsApiKey(s.Provider) && string.IsNullOrWhiteSpace(s.ApiKey)) return null;

        string model = string.IsNullOrWhiteSpace(s.Model) ? DefaultModel(s.Provider) : s.Model.Trim();
        string baseUrl = (string.IsNullOrWhiteSpace(s.BaseUrl) ? DefaultBaseUrl(s.Provider) : s.BaseUrl.Trim()).TrimEnd('/');
        return s.Provider switch
        {
            AiProvider.Claude => new ClaudeClient(http, baseUrl, s.ApiKey.Trim(), model),
            AiProvider.Ollama => new OllamaClient(http, baseUrl, model),
            AiProvider.OpenAI => new OpenAICompatibleClient(http, baseUrl, s.ApiKey.Trim(), model, "OpenAI", jsonMode: true),
            AiProvider.Gemini => new OpenAICompatibleClient(http, baseUrl, s.ApiKey.Trim(), model, "Gemini", jsonMode: false),
            AiProvider.OpenAICompatible => new OpenAICompatibleClient(http, baseUrl, s.ApiKey.Trim(), model, "AI", jsonMode: false),
            _ => null
        };
    }

    internal static async Task<JsonNode> SendJsonAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = body.Length > 300 ? body[..300] : body;
            throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {detail}", null, response.StatusCode);
        }
        return JsonNode.Parse(body) ?? throw new JsonException("Empty response");
    }
}

/// <summary>Anthropic Messages API.</summary>
public sealed class ClaudeClient(HttpClient http, string baseUrl, string apiKey, string model) : ILlmClient
{
    public string DisplayName => "Claude";

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model,
                max_tokens = 400,
                temperature = 0,
                system,
                messages = new[] { new { role = "user", content = user } }
            })
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        var json = await LlmClientFactory.SendJsonAsync(http, request, ct).ConfigureAwait(false);
        return json["content"]?.AsArray()
                   .Where(b => (string?)b?["type"] == "text")
                   .Select(b => (string?)b?["text"])
                   .FirstOrDefault()
               ?? throw new JsonException("No text in Claude response");
    }
}

/// <summary>OpenAI Chat Completions, also used for Gemini's and other OpenAI-compatible endpoints.</summary>
public sealed class OpenAICompatibleClient(HttpClient http, string baseUrl, string apiKey, string model, string name, bool jsonMode)
    : ILlmClient
{
    public string DisplayName => name;

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user },
            }
        };
        if (jsonMode) body["response_format"] = new JsonObject { ["type"] = "json_object" };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrEmpty(apiKey))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var json = await LlmClientFactory.SendJsonAsync(http, request, ct).ConfigureAwait(false);
        return (string?)json["choices"]?[0]?["message"]?["content"]
               ?? throw new JsonException($"No message in {name} response");
    }
}

/// <summary>Ollama running locally: free and offline.</summary>
public sealed class OllamaClient(HttpClient http, string baseUrl, string model) : ILlmClient
{
    public string DisplayName => "Ollama";

    public async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/chat")
        {
            Content = JsonContent.Create(new
            {
                model,
                stream = false,
                format = "json",
                options = new { temperature = 0 },
                messages = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user },
                }
            })
        };
        var json = await LlmClientFactory.SendJsonAsync(http, request, ct).ConfigureAwait(false);
        return (string?)json["message"]?["content"] ?? throw new JsonException("No message in Ollama response");
    }
}
