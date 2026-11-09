using System.IO;
using System.Windows;
using SDM.App.Localization;
using SDM.Core.Util;

namespace SDM.App.ViewModels;

/// <summary>"Verify checksum": computes the file's hash and compares it with the one from the website.</summary>
public sealed class HashViewModel : ObservableObject
{
    private readonly string _path;
    private HashAlgorithmKind _algorithm = HashAlgorithmKind.Sha256;
    private string _expected = "";
    private string _computed = "";
    private double _progress;
    private bool _busy;
    private CancellationTokenSource? _cts;

    public HashViewModel(string path)
    {
        _path = path;
        CopyCommand = new RelayCommand(() =>
        {
            ClipboardWatcher.IgnoreNext(Computed);
            try { Clipboard.SetText(Computed); } catch { /* clipboard busy */ }
        }, () => Computed.Length > 0);
        CloseCommand = new RelayCommand(() => { _cts?.Cancel(); CloseRequested?.Invoke(); });
        _ = ComputeAsync();
    }

    public event Action? CloseRequested;
    public RelayCommand CopyCommand { get; }
    public RelayCommand CloseCommand { get; }

    public string FileName => Path.GetFileName(_path);

    public IReadOnlyList<HashAlgorithmKind> Algorithms { get; } =
        new[] { HashAlgorithmKind.Md5, HashAlgorithmKind.Sha1, HashAlgorithmKind.Sha256, HashAlgorithmKind.Sha512 };

    public HashAlgorithmKind Algorithm
    {
        get => _algorithm;
        set { if (Set(ref _algorithm, value)) _ = ComputeAsync(); }
    }

    /// <summary>The checksum pasted from the website; its length picks the algorithm.</summary>
    public string Expected
    {
        get => _expected;
        set
        {
            if (!Set(ref _expected, value)) return;
            if (HashVerifier.Detect(value) is { } kind && kind != Algorithm) Algorithm = kind;
            RaiseResult();
        }
    }

    public string Computed { get => _computed; private set { if (Set(ref _computed, value)) RaiseResult(); } }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool Busy { get => _busy; private set => Set(ref _busy, value); }

    /// <summary>"Match", "Mismatch" or "" (nothing to compare yet): drives the colour.</summary>
    public string ResultKey => Computed.Length == 0 || HashVerifier.Normalize(Expected) is null
        ? ""
        : HashVerifier.Matches(Computed, Expected) ? "Match" : "Mismatch";

    public string ResultText => ResultKey switch
    {
        "Match" => Loc.Get("HashMatch"),
        "Mismatch" => Loc.Get("HashMismatch"),
        _ => Expected.Trim().Length > 0 ? Loc.Get("HashNotRecognized") : Loc.Get("HashPasteHint")
    };

    private void RaiseResult()
    {
        OnPropertyChanged(nameof(ResultKey));
        OnPropertyChanged(nameof(ResultText));
    }

    private async Task ComputeAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Busy = true;
        Computed = "";
        Progress = 0;
        try
        {
            var progress = new Progress<double>(p => Progress = p);
            string hash = await HashVerifier.ComputeAsync(_path, _algorithm, progress, cts.Token);
            if (!cts.IsCancellationRequested) Computed = hash;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Computed = ""; _expected = ex.Message; OnPropertyChanged(nameof(Expected)); }
        finally { if (_cts == cts) Busy = false; }
    }
}
