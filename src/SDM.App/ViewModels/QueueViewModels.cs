using SDM.App.Localization;
using SDM.Core;
using SDM.Core.Engine;
using SDM.Core.Settings;
using SDM.Core.Util;

namespace SDM.App.ViewModels;

/// <summary>A queue choice in the Add / Batch windows.</summary>
public sealed record QueueOption(string Id, string Name)
{
    public static IReadOnlyList<QueueOption> From(SmartDownloadService service) =>
        service.AllQueues.Select(q => new QueueOption(q.Id, q.Id == DownloadQueue.MainQueueId ? Loc.Get("MainQueue") : q.Name)).ToList();
}

/// <summary>One queue in the sidebar: click filters the list; right-click starts or pauses the whole queue.</summary>
public sealed class QueueItemViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private int _count;

    public QueueItemViewModel(MainViewModel main, QueueDefinition queue, bool isMain)
    {
        _main = main;
        Id = queue.Id;
        Name = isMain ? Loc.Get("MainQueue") : queue.Name;
        string limit = string.Format(Loc.Get("QueueParallel"), queue.MaxConcurrent);
        Detail = queue.SpeedLimitKBps > 0 ? $"{limit} · {FileNameHelper.FormatSize(queue.SpeedLimitKBps * 1024)}/s" : limit;
        StartCommand = new RelayCommand(() => main.StartQueue(Id));
        PauseCommand = new RelayCommand(() => main.PauseQueue(Id));
        MoveHereCommand = new RelayCommand(() => main.MoveSelectedTo(Id));
    }

    public string Id { get; }
    public string Name { get; }
    public string Detail { get; }
    public RelayCommand StartCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand MoveHereCommand { get; }

    public int Count { get => _count; set => Set(ref _count, value); }

    public bool IsChecked
    {
        get => _main.Filter == "queue:" + Id;
        set { if (value) _main.Filter = "queue:" + Id; }
    }

    public void RefreshChecked() => OnPropertyChanged(nameof(IsChecked));
}

/// <summary>Editable row on the Queues settings page.</summary>
public sealed class QueueRow : ObservableObject
{
    private string _name = "";
    private int _maxConcurrent = 1;
    private long _speedLimit;

    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get => _name; set => Set(ref _name, value); }
    public int MaxConcurrent { get => _maxConcurrent; set => Set(ref _maxConcurrent, Math.Clamp(value, 1, 20)); }
    public long SpeedLimitKBps { get => _speedLimit; set => Set(ref _speedLimit, Math.Max(0, value)); }

    public static QueueRow From(QueueDefinition q) =>
        new() { Id = q.Id, Name = q.Name, MaxConcurrent = q.MaxConcurrent, SpeedLimitKBps = q.SpeedLimitKBps };

    public QueueDefinition ToDefinition() =>
        new() { Id = Id, Name = Name.Trim(), MaxConcurrent = MaxConcurrent, SpeedLimitKBps = SpeedLimitKBps };
}
