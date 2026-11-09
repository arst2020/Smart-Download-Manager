using SDM.Core.Classification;

namespace SDM.Core.Tests;

public class EpisodeParserTests
{
    [Theory]
    [InlineData("Dark.S01E03.1080p.WEB-DL.x264.mkv", "Dark", 1, 3)]
    [InlineData("the.office.s02e10.720p.mp4", "The Office", 2, 10)]
    [InlineData("Breaking Bad - 3x07 - One Minute.avi", "Breaking Bad", 3, 7)]
    [InlineData("[SubsPlease] Frieren S01E12 (1080p).mkv", "Frieren", 1, 12)]
    [InlineData("Shogun.2024.S01E05.mkv", "Shogun", 1, 5)]
    [InlineData("Severance Season 2 Episode 4.mp4", "Severance", 2, 4)]
    [InlineData("جیران فصل ۱ قسمت ۱۲.mp4", "جیران", 1, 12)]
    [InlineData("شهرزاد قسمت ۷.mkv", "شهرزاد", 1, 7)]
    [InlineData("Dark.S01E03.srt", "Dark", 1, 3)]
    public void Parses_common_episode_names(string file, string show, int season, int episode)
    {
        Assert.True(EpisodeParser.TryParse(file, out var info, out var confidence));
        Assert.Equal(show, info.Show);
        Assert.Equal(season, info.Season);
        Assert.Equal(episode, info.Episode);
        Assert.True(confidence >= 0.8);
    }

    [Theory]
    [InlineData("ubuntu-24.04-desktop-amd64.iso")]
    [InlineData("invoice_2024.pdf")]
    [InlineData("setup.exe")]
    [InlineData("Dune.Part.Two.2024.1080p.mkv")]
    public void Ignores_non_episodes(string file)
    {
        Assert.False(EpisodeParser.TryParse(file, out _, out _));
    }

    [Fact]
    public void Non_video_episode_names_get_lower_confidence()
    {
        Assert.True(EpisodeParser.TryParse("Report S01E02.pdf", out _, out var confidence));
        Assert.True(confidence < ClassifierPipeline.ConfidenceThreshold);
    }
}

public class EpisodeClassifierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sdm-test-" + Guid.NewGuid().ToString("N"));

    public EpisodeClassifierTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Creates_show_and_season_folder()
    {
        var classifier = new EpisodeClassifier(() => _root);
        var r = await classifier.ClassifyAsync(new ClassificationContext("Dark.S02E05.mkv"));
        Assert.NotNull(r);
        Assert.Equal(Path.Combine(_root, "Dark", "Season 02"), r!.Folder);
    }

    [Fact]
    public async Task Reuses_existing_folders_with_different_spelling()
    {
        Directory.CreateDirectory(Path.Combine(_root, "The Office (US)", "S2"));
        Directory.CreateDirectory(Path.Combine(_root, "Breaking.Bad", "فصل ۳"));
        var classifier = new EpisodeClassifier(() => _root);

        var office = await classifier.ClassifyAsync(new ClassificationContext("The.Office.US.S02E01.mkv"));
        Assert.Equal(Path.Combine(_root, "The Office (US)", "S2"), office!.Folder);

        var bb = await classifier.ClassifyAsync(new ClassificationContext("Breaking Bad 3x07.mkv"));
        Assert.Equal(Path.Combine(_root, "Breaking.Bad", "فصل ۳"), bb!.Folder);
    }
}

public class UserRuleClassifierTests
{
    [Fact]
    public async Task Keyword_rule_wins_and_adds_season_folder()
    {
        var rules = new List<UserRule>
        {
            new() { Name = "Dark", Keywords = { "dark" }, TargetFolder = Path.Combine("X", "Dark") }
        };
        var pipeline = new ClassifierPipeline(new IClassifier[]
        {
            new UserRuleClassifier(() => rules),
            new EpisodeClassifier(() => "Series"),
            new TypeClassifier(),
        });

        var r = await pipeline.ClassifyAsync(new ClassificationContext("Dark.S01E03.mkv"));
        Assert.Equal("user-rule", r.Source);
        Assert.Equal(Path.Combine("X", "Dark", "Season 01"), r.Folder);
    }

    [Fact]
    public void Keywords_match_whole_words_only()
    {
        var rule = new UserRule { Name = "Dark", Keywords = { "dark" }, TargetFolder = "D" };
        Assert.True(UserRuleClassifier.Matches(rule, new ClassificationContext("Dark.S01E01.mkv")));
        Assert.False(UserRuleClassifier.Matches(rule, new ClassificationContext("Darkness.mkv")));
    }

    [Fact]
    public void Domain_and_extension_conditions_are_combined()
    {
        var rule = new UserRule { Name = "Dev", Domains = { "github.com" }, Extensions = { "zip" }, TargetFolder = "Dev" };
        var url = new Uri("https://objects.github.com/x/repo.zip");
        Assert.True(UserRuleClassifier.Matches(rule, new ClassificationContext("repo.zip", url)));
        Assert.False(UserRuleClassifier.Matches(rule, new ClassificationContext("repo.tar", url)));
        Assert.False(UserRuleClassifier.Matches(rule, new ClassificationContext("repo.zip", new Uri("https://gitlab.com/a.zip"))));
    }

    [Fact]
    public void Persian_keyword_matches()
    {
        var rule = new UserRule { Name = "جیران", Keywords = { "جیران" }, TargetFolder = "J" };
        Assert.True(UserRuleClassifier.Matches(rule, new ClassificationContext("جیران فصل ۱ قسمت ۲.mp4")));
    }

    [Fact]
    public void Invalid_regex_does_not_throw()
    {
        var rule = new UserRule { Name = "bad", Pattern = "([", TargetFolder = "B" };
        Assert.False(UserRuleClassifier.Matches(rule, new ClassificationContext("a.txt")));
    }
}

public class TypeClassifierTests
{
    [Theory]
    [InlineData("movie.mkv", "Video")]
    [InlineData("song.FLAC", "Music")]
    [InlineData("paper.pdf", "Documents")]
    [InlineData("archive.7z", "Compressed")]
    [InlineData("setup.msi", "Programs")]
    public async Task Classifies_by_extension(string file, string category)
    {
        var r = await new TypeClassifier().ClassifyAsync(new ClassificationContext(file));
        Assert.Equal(category, r!.Category);
    }

    [Fact]
    public async Task Falls_back_to_magic_bytes()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, "%PDF-1.7 test"u8.ToArray());
            var r = await new TypeClassifier().ClassifyAsync(new ClassificationContext("download", FilePath: path));
            Assert.Equal("Documents", r!.Category);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Uses_renamed_category_folders()
    {
        var r = await new TypeClassifier(c => c == "Video" ? "فیلم" : c).ClassifyAsync(new ClassificationContext("a.mp4"));
        Assert.Equal("فیلم", r!.Folder);
    }
}
