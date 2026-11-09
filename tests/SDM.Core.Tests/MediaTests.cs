using System.Net;
using SDM.Core.Classification;
using SDM.Core.Engine;
using SDM.Core.Media;
using SDM.Core.Media.Ai;
using SDM.Core.Settings;

namespace SDM.Core.Tests;

public class MediaNameParserTests
{
    [Theory]
    [InlineData("Dune.Part.Two.2024.2160p.WEB-DL.mkv", "Dune Part Two", 2024)]
    [InlineData("Oppenheimer (2023) [1080p].mp4", "Oppenheimer", 2023)]
    [InlineData("the_matrix_1999.avi", "The Matrix", 1999)]
    public void Detects_movies(string file, string title, int year)
    {
        var m = MediaNameParser.Parse(file);
        Assert.Equal(MediaKind.Movie, m.Kind);
        Assert.Equal(title, m.Title);
        Assert.Equal(year, m.Year);
    }

    [Fact]
    public void Keeps_year_of_series()
    {
        var m = MediaNameParser.Parse("Shogun.2024.S01E05.1080p.mkv");
        Assert.Equal(MediaKind.Series, m.Kind);
        Assert.Equal("Shogun", m.Title);
        Assert.Equal(2024, m.Year);
        Assert.Equal(5, m.Episode);
    }

    [Fact]
    public void Video_without_clues_is_uncertain()
    {
        var m = MediaNameParser.Parse("VID_0042.mp4");
        Assert.True(m.Confidence < ClassifierPipeline.ConfidenceThreshold);
    }
}

public class MediaNamerTests
{
    [Fact]
    public void Standard_names()
    {
        var ep = new MediaInfo { Kind = MediaKind.Series, Title = "Dark", Year = 2017, Season = 1, Episode = 3, EpisodeTitle = "Past and Present" };
        Assert.Equal("Dark - S01E03 - Past and Present.mkv", MediaNamer.StandardFileName(ep, "x.mkv"));

        var movie = new MediaInfo { Kind = MediaKind.Movie, Title = "Dune: Part Two", Year = 2024 };
        Assert.Equal("Dune - Part Two (2024).mp4", MediaNamer.StandardFileName(movie, "a.mp4"));

        Assert.Null(MediaNamer.StandardFileName(new MediaInfo { Kind = MediaKind.Software, Title = "x" }, "setup.exe"));
    }

    [Fact]
    public void Reuses_existing_show_folder_with_year()
    {
        string root = Path.Combine(Path.GetTempPath(), "sdm-namer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Dark (2017)", "Season 1"));
        try
        {
            var m = new MediaInfo { Kind = MediaKind.Series, Title = "Dark", Season = 1, Episode = 2 };
            Assert.Equal(Path.Combine(root, "Dark (2017)", "Season 1"), MediaNamer.SeriesFolder(root, m));

            var m2 = m with { Title = "Severance", Year = 2022, Season = 2 };
            Assert.Equal(Path.Combine(root, "Severance (2022)", "Season 02"), MediaNamer.SeriesFolder(root, m2));
        }
        finally { Directory.Delete(root, true); }
    }
}

public class LlmTests
{
    [Fact]
    public void Parses_answer_with_code_fence_and_text()
    {
        var m = LlmMediaDetector.Parse("""
            Sure! ```json
            {"kind":"series","title":"The Office","year":2005,"season":null,"episode":"4","confidence":0.8,"reason":"US sitcom"}
            ```
            """);
        Assert.NotNull(m);
        Assert.Equal(MediaKind.Series, m!.Kind);
        Assert.Equal("The Office", m.Title);
        Assert.Equal(1, m.Season); // episode without season → season 1
        Assert.Equal(4, m.Episode);
    }

    [Fact]
    public void Garbage_returns_null()
    {
        Assert.Null(LlmMediaDetector.Parse("I don't know"));
    }

    [Fact]
    public async Task Claude_request_shape()
    {
        var fake = new FakeHttp((_, _) => FakeHttp.Json("""{"content":[{"type":"text","text":"{\"kind\":\"movie\"}"}]}"""));
        var client = LlmClientFactory.Create(new AiSettings { Provider = AiProvider.Claude, ApiKey = "sk-test" }, fake.Client())!;

        string answer = await client.CompleteAsync("sys", "user", default);

        Assert.Equal("{\"kind\":\"movie\"}", answer);
        var (req, body) = fake.Requests.Single();
        Assert.Equal("https://api.anthropic.com/v1/messages", req.RequestUri!.ToString());
        Assert.Equal("sk-test", req.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", req.Headers.GetValues("anthropic-version").Single());
        Assert.True(body!.Contains("\"system\":\"sys\"") && body.Contains("claude-haiku-5-5"));
    }

    [Theory]
    [InlineData(AiProvider.OpenAI, "https://api.openai.com/v1/chat/completions", true)]
    [InlineData(AiProvider.Gemini, "https://generativelanguage.googleapis.com/v1beta/openai/chat/completions", true)]
    [InlineData(AiProvider.OpenAICompatible, "http://localhost:1234/v1/chat/completions", false)]
    public async Task OpenAI_style_request_shape(AiProvider provider, string url, bool withKey)
    {
        var fake = new FakeHttp((_, _) => FakeHttp.Json("""{"choices":[{"message":{"content":"{}"}}]}"""));
        var client = LlmClientFactory.Create(new AiSettings { Provider = provider, ApiKey = withKey ? "k" : "" }, fake.Client())!;

        await client.CompleteAsync("s", "u", default);

        var (req, _) = fake.Requests.Single();
        Assert.Equal(url, req.RequestUri!.ToString());
        Assert.Equal(withKey, req.Headers.Authorization is not null);
    }

    [Fact]
    public async Task Ollama_request_shape()
    {
        var fake = new FakeHttp((_, _) => FakeHttp.Json("""{"message":{"content":"{}"}}"""));
        var client = LlmClientFactory.Create(new AiSettings { Provider = AiProvider.Ollama, Model = "qwen2.5" }, fake.Client())!;

        await client.CompleteAsync("s", "u", default);

        var (req, body) = fake.Requests.Single();
        Assert.Equal("http://localhost:11434/api/chat", req.RequestUri!.ToString());
        Assert.True(body!.Contains("\"format\":\"json\"") && body.Contains("qwen2.5"));
    }

    [Fact]
    public void Cloud_providers_without_key_are_off()
    {
        Assert.Null(LlmClientFactory.Create(new AiSettings { Provider = AiProvider.Claude }, new HttpClient()));
        Assert.Null(LlmClientFactory.Create(new AiSettings { Provider = AiProvider.None, ApiKey = "x" }, new HttpClient()));
    }

    [Fact]
    public async Task Api_errors_include_status_and_detail()
    {
        var fake = new FakeHttp((_, _) => FakeHttp.Json("""{"error":"invalid key"}""", HttpStatusCode.Unauthorized));
        var client = LlmClientFactory.Create(new AiSettings { Provider = AiProvider.OpenAI, ApiKey = "bad" }, fake.Client())!;
        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.CompleteAsync("s", "u", default));
        Assert.True(ex.Message.Contains("401") && ex.Message.Contains("invalid key"));
    }
}

public class TmdbTests
{
    [Fact]
    public async Task Api_key_goes_in_query_and_year_is_parsed()
    {
        var fake = new FakeHttp((_, _) => FakeHttp.Json("""{"results":[{"id":2316,"name":"The Office","first_air_date":"2005-03-24"}]}"""));
        var tmdb = new TmdbClient(fake.Client(), "abc123");

        var match = await tmdb.FindSeriesAsync("the office us", null, default);

        Assert.Equal(new TmdbMatch(2316, "The Office", 2005), match);
        string url = fake.Requests.Single().Request.RequestUri!.AbsoluteUri;
        Assert.True(url.StartsWith("https://api.themoviedb.org/3/search/tv?query=the%20office%20us") && url.EndsWith("api_key=abc123"));
    }

    [Fact]
    public async Task Read_access_token_uses_bearer_and_retries_without_year()
    {
        int calls = 0;
        var fake = new FakeHttp((_, _) => ++calls == 1
            ? FakeHttp.Json("""{"results":[]}""")
            : FakeHttp.Json("""{"results":[{"id":693134,"title":"Dune: Part Two","release_date":"2024-02-27"}]}"""));
        var tmdb = new TmdbClient(fake.Client(), "eyJhbGciOiJIUzI1NiJ9.token");

        var match = await tmdb.FindMovieAsync("Dune Part Two", 2023, default);

        Assert.Equal(2024, match!.Year);
        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal("Bearer", fake.Requests[0].Request.Headers.Authorization!.Scheme);
        Assert.True(fake.Requests[0].Request.RequestUri!.ToString().Contains("year=2023"));
        Assert.False(fake.Requests[1].Request.RequestUri!.ToString().Contains("year="));
    }
}

public class MediaServerTests
{
    private static MediaServerSettings Emby => new() { Type = MediaServerType.Emby, Url = "http://localhost:8096", ApiKey = "key" };

    [Fact]
    public async Task Reads_libraries_from_array()
    {
        var fake = new FakeHttp((_, _) => FakeHttp.Json("""
            [{"Name":"TV Shows","CollectionType":"tvshows","Locations":["E:\\Media\\TV"]},
             {"Name":"Movies","CollectionType":"movies","Locations":["E:\\Media\\Movies"]},
             {"Name":"Collections","CollectionType":"boxsets","Locations":[]}]
            """));
        var libs = await new MediaServerClient(fake.Client(), Emby).GetLibrariesAsync(default);

        Assert.Equal(2, libs.Count);
        Assert.Equal(MediaKind.Series, libs[0].Kind);
        Assert.Equal("key", fake.Requests[0].Request.Headers.GetValues("X-Emby-Token").Single());
    }

    [Fact]
    public async Task Falls_back_to_emby_prefix_and_items_wrapper()
    {
        var fake = new FakeHttp((req, _) => req.RequestUri!.AbsolutePath.StartsWith("/emby/")
            ? FakeHttp.Json("""{"Items":[{"Name":"Music","CollectionType":"music","Locations":["/music"]}]}""")
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var client = new MediaServerClient(fake.Client(), Emby);

        var libs = await client.GetLibrariesAsync(default);
        await client.RefreshAsync(default);

        Assert.Equal(MediaKind.Music, libs.Single().Kind);
        Assert.Equal("http://localhost:8096/emby/Library/Refresh", fake.Requests.Last().Request.RequestUri!.ToString());
    }
}

public class MediaAdvisorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sdm-adv-" + Guid.NewGuid().ToString("N"));

    public MediaAdvisorTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class FakeLlm(string answer) : ILlmClient
    {
        public int Calls;
        public string DisplayName => "FakeAI";
        public Task<string> CompleteAsync(string system, string user, CancellationToken ct) { Calls++; return Task.FromResult(answer); }
    }

    private AppSettings Settings() => new()
    {
        DownloadDirectory = Path.Combine(_root, "dl"),
        Ai = new AiSettings { Provider = AiProvider.Claude, ApiKey = "x" },
    };

    [Fact]
    public async Task Asks_ai_only_when_unsure()
    {
        var llm = new FakeLlm("""{"kind":"movie","title":"Inception","year":2010,"confidence":0.9,"reason":"well-known film"}""");
        var advisor = new MediaAdvisor(Settings, new HttpClient(), _ => llm);

        var sure = await advisor.SuggestAsync(new SuggestRequest("Dark.S01E03.mkv"));
        Assert.Equal(0, llm.Calls);
        Assert.Equal("local", sure.Media.Source);

        var unsure = await advisor.SuggestAsync(new SuggestRequest("incptn_hd_final.mp4"));
        Assert.Equal(1, llm.Calls);
        Assert.Equal("FakeAI", unsure.Media.Source);
        Assert.Equal(Path.Combine(_root, "dl", "Movies", "Inception (2010)"), unsure.Folder);
        Assert.Equal("Inception (2010).mp4", unsure.StandardFileName);
    }

    [Fact]
    public async Task Ai_failure_falls_back_to_local()
    {
        var advisor = new MediaAdvisor(Settings, new HttpClient(), _ => new ThrowingLlm());
        var s = await advisor.SuggestAsync(new SuggestRequest("VID_0042.mp4"));
        Assert.Equal("Video", s.Folder);
        Assert.True(advisor.LastWarning!.Contains("offline"));
    }

    private sealed class ThrowingLlm : ILlmClient
    {
        public string DisplayName => "X";
        public Task<string> CompleteAsync(string s, string u, CancellationToken ct) => throw new HttpRequestException("offline");
    }

    [Fact]
    public async Task Uses_emby_library_and_tmdb_title()
    {
        string tv = Path.Combine(_root, "Media", "TV");
        Directory.CreateDirectory(tv);
        string libJson = $$"""[{"Name":"TV Shows","CollectionType":"tvshows","Locations":[{{System.Text.Json.JsonSerializer.Serialize(tv)}}]}]""";
        var fake = new FakeHttp((req, _) => req.RequestUri!.Host switch
        {
            "localhost" => FakeHttp.Json(libJson),
            _ when req.RequestUri.AbsolutePath.Contains("/episode/") => FakeHttp.Json("""{"name":"Secrets"}"""),
            _ => FakeHttp.Json("""{"results":[{"id":70523,"name":"Dark","first_air_date":"2017-12-01"}]}"""),
        });
        AppSettings Cfg()
        {
            var s = Settings();
            s.TmdbApiKey = "k";
            s.MediaServer = new MediaServerSettings { Type = MediaServerType.Emby, Url = "http://localhost:8096", ApiKey = "e" };
            return s;
        }
        var advisor = new MediaAdvisor(Cfg, fake.Client(), _ => null);

        var s = await advisor.SuggestAsync(new SuggestRequest("dark.s01e01.1080p.mkv"));

        Assert.Equal("TV Shows", s.Library!.Name);
        Assert.Equal(Path.Combine(tv, "Dark (2017)", "Season 01"), s.Folder);
        Assert.Equal("Dark - S01E01 - Secrets.mkv", s.StandardFileName);
        Assert.Equal("local + TMDB", s.Media.Source);
        Assert.True(s.Alternatives.Any(a => a.Label == "Series"));
    }

    [Fact]
    public async Task User_rule_wins()
    {
        AppSettings Cfg()
        {
            var s = Settings();
            s.Rules.Add(new UserRule { Name = "Kids", Keywords = { "peppa" }, TargetFolder = Path.Combine(_root, "Kids") });
            return s;
        }
        var advisor = new MediaAdvisor(Cfg, new HttpClient(), _ => null);
        var s = await advisor.SuggestAsync(new SuggestRequest("Peppa.Pig.S02E04.mp4"));
        Assert.Equal("Kids", s.RuleName);
        Assert.Equal(Path.Combine(_root, "Kids", "Season 02"), s.Folder);
    }
}

public class ConnectionGateTests
{
    [Fact]
    public async Task Reduce_lowers_concurrency()
    {
        var gate = new ConnectionGate(4);
        for (int i = 0; i < 4; i++) await gate.WaitAsync(default);
        Assert.Equal(2, gate.Reduce());

        // Two releases are swallowed by the reduction, two free real slots.
        for (int i = 0; i < 4; i++) gate.Release();
        Assert.True(await TryEnter(gate));
        Assert.True(await TryEnter(gate));
        Assert.False(await TryEnter(gate));
    }

    private static async Task<bool> TryEnter(ConnectionGate g)
    {
        using var cts = new CancellationTokenSource(100);
        try { await g.WaitAsync(cts.Token); return true; } catch (OperationCanceledException) { return false; }
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void Round_trips_with_enum_names()
    {
        string path = Path.Combine(Path.GetTempPath(), "sdm-settings-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var s = new AppSettings { Ai = new AiSettings { Provider = AiProvider.Gemini }, MediaServer = { Type = MediaServerType.Jellyfin } };
            SettingsStore.Save(s, path);
            string json = File.ReadAllText(path);
            Assert.True(json.Contains("\"Gemini\"") && json.Contains("\"Jellyfin\""));
            var back = SettingsStore.Load(path);
            Assert.Equal(AiProvider.Gemini, back.Ai.Provider);
            Assert.Equal(32, back.MaxSegments);
        }
        finally { File.Delete(path); }
    }
}
