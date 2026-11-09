using System.Text;
using SDM.Core;
using SDM.Core.Classification;
using SDM.Core.Engine;
using SDM.Core.Media;
using SDM.Core.Models;
using SDM.Core.Settings;
using SDM.Core.Util;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

var options = ParseOptions(args.Skip(1).ToArray(), out var positional);
var settings = options.TryGetValue("settings", out var sp) ? SettingsStore.Load(sp) : SettingsStore.Load();
if (options.TryGetValue("out", out var outDir)) settings.DownloadDirectory = Path.GetFullPath(outDir);
if (options.TryGetValue("series", out var seriesDir)) settings.SeriesDirectory = Path.GetFullPath(seriesDir);
if (options.TryGetValue("connections", out var seg)) settings.MaxSegments = int.Parse(seg);
if (options.TryGetValue("limit", out var lim)) settings.GlobalSpeedLimitKBps = long.Parse(lim);
if (options.ContainsKey("no-organize")) settings.AutoOrganize = false;
if (options.ContainsKey("keep-name")) settings.RenameToStandard = false;

switch (args[0])
{
    case "get":
        if (positional.Count == 0) { Console.Error.WriteLine("usage: sdm get <url> [...]"); return 2; }
        return await GetAsync(settings, positional);

    case "classify":
        if (positional.Count == 0) { Console.Error.WriteLine("usage: sdm classify <file name> [...]"); return 2; }
        return await ClassifyAsync(settings, positional);

    case "settings":
        Console.WriteLine(SettingsStore.DefaultPath);
        return 0;

    default:
        Console.Error.WriteLine($"Unknown command '{args[0]}'.");
        PrintHelp();
        return 2;
}

static async Task<int> GetAsync(AppSettings settings, List<string> urls)
{
    using var service = new SmartDownloadService(settings);
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Console.WriteLine();
        Console.WriteLine("Pausing... progress is saved; run the same command again to resume.");
        foreach (var j in service.Queue.Jobs) service.Queue.Pause(j);
        cts.Cancel();
    };

    var jobs = urls.Select(u => service.Add(new Uri(u))).ToList();
    var lastDraw = DateTime.MinValue;
    var drawLock = new object();
    foreach (var job in jobs)
    {
        job.Changed += j =>
        {
            lock (drawLock)
            {
                if (j.Status == DownloadStatus.Downloading && (DateTime.Now - lastDraw).TotalMilliseconds < 250) return;
                lastDraw = DateTime.Now;
                Draw(j);
            }
        };
    }

    try { await service.Queue.WhenIdleAsync(); } catch (OperationCanceledException) { }

    Console.WriteLine();
    int failures = 0;
    foreach (var job in jobs)
    {
        switch (job.Status)
        {
            case DownloadStatus.Completed:
                string where = job.Classification is { } c ? $"  [{c.Category} · {c.Source}]" : "";
                Console.WriteLine($"✔ {job.FilePath}{where}");
                break;
            case DownloadStatus.Paused:
                Console.WriteLine($"⏸ {job.Request.Url} (paused)");
                break;
            default:
                failures++;
                Console.WriteLine($"✖ {job.Request.Url}: {job.Status} {job.Error}");
                break;
        }
    }
    return failures == 0 ? 0 : 1;
}

static void Draw(DownloadJob job)
{
    var p = job.Progress;
    string name = job.FileName ?? job.Request.Url.Segments.LastOrDefault() ?? "";
    string pct = p.Fraction is double f ? $"{f * 100,5:0.0}%" : "  ?  ";
    string size = p.Total is long t ? $"{FileNameHelper.FormatSize(p.Downloaded)} / {FileNameHelper.FormatSize(t)}"
                                    : FileNameHelper.FormatSize(p.Downloaded);
    string speed = $"{FileNameHelper.FormatSize((long)p.BytesPerSecond)}/s";
    string eta = p.Remaining is TimeSpan r ? $"ETA {r:hh\\:mm\\:ss}" : "";
    string line = $"{job.Status,-11} {pct}  {size}  {speed}  {p.ActiveSegments} conn  {eta}  {name}";
    int width = Console.IsOutputRedirected ? 200 : Math.Max(20, Console.WindowWidth - 1);
    if (line.Length > width) line = line[..width];
    Console.Write("\r" + line.PadRight(width));
}

static async Task<int> ClassifyAsync(AppSettings settings, List<string> names)
{
    using var service = new SmartDownloadService(settings);
    foreach (var name in names)
    {
        var s = await service.Advisor.SuggestAsync(new SuggestRequest(name));
        string folder = Path.GetFullPath(Path.Combine(settings.DownloadDirectory, s.Folder));
        Console.WriteLine(name);
        Console.WriteLine($"  {s.Describe()}  [{s.Media.Kind} · {s.Media.Source} · {s.Media.Confidence:0.00}]");
        Console.WriteLine($"  → {folder}" + (s.Library is { } lib ? $"  ({lib.Server}: {lib.Name})" : ""));
        if (s.StandardFileName is { } std && std != name) Console.WriteLine($"  name: {std}");
        if (s.RuleName is { } rule) Console.WriteLine($"  rule: {rule}");
        if (s.Media.Reason is { } why) Console.WriteLine($"  why: {why}");
        if (service.Advisor.LastWarning is { } w) Console.WriteLine($"  ! {w}");
    }
    return 0;
}

static Dictionary<string, string> ParseOptions(string[] args, out List<string> positional)
{
    var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    positional = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--"))
        {
            string key = args[i][2..];
            bool isFlag = key is "no-organize" or "keep-name";
            options[key] = !isFlag && i + 1 < args.Length ? args[++i] : "true";
        }
        else positional.Add(args[i]);
    }
    return options;
}

static void PrintHelp()
{
    Console.WriteLine("""
        Smart Download Manager CLI

        sdm get <url> [url...]       download (Ctrl+C pauses; run again to resume)
            --out <dir>              download directory
            --series <dir>           series root (default: <out>/Series)
            --connections <n>        connections per file: 1, 4, 8, 16, 32 (default 32)
            --limit <KB/s>           global speed limit
            --no-organize            keep files in the download directory
            --keep-name              do not rename to the Emby/Jellyfin standard
        sdm classify <name> [...]    show the suggestion (folder, standard name) for a file name
        sdm settings                 print the settings file path
        """);
}
