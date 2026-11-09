namespace SDM.Core.Classification;

public sealed record ClassificationContext(
    string FileName,
    Uri? SourceUrl = null,
    string? ContentType = null,
    string? PageTitle = null,
    string? FilePath = null);

public sealed record EpisodeInfo(string Show, int Season, int Episode);

/// <param name="Category">Category key, e.g. "Series", "Video", "Documents" or a rule name.</param>
/// <param name="Folder">Destination folder; relative paths are resolved against the download directory.</param>
/// <param name="Confidence">0..1. The pipeline stops at the first result of 0.8 or more.</param>
/// <param name="Source">Which classifier produced the result.</param>
public sealed record ClassificationResult(
    string Category,
    string Folder,
    double Confidence,
    string Source,
    EpisodeInfo? Episode = null,
    string? Explanation = null);

public interface IClassifier
{
    string Name { get; }
    ValueTask<ClassificationResult?> ClassifyAsync(ClassificationContext context, CancellationToken ct = default);
}

/// <summary>
/// Runs classifiers in order and returns the first confident answer.
/// Cheap local classifiers come first; an LLM classifier (phase 3) goes last.
/// </summary>
public sealed class ClassifierPipeline
{
    public const double ConfidenceThreshold = 0.8;

    private readonly IReadOnlyList<IClassifier> _classifiers;

    public ClassifierPipeline(IEnumerable<IClassifier> classifiers) => _classifiers = classifiers.ToList();

    public IReadOnlyList<IClassifier> Classifiers => _classifiers;

    public async Task<ClassificationResult> ClassifyAsync(ClassificationContext context, CancellationToken ct = default)
    {
        ClassificationResult? best = null;
        foreach (var classifier in _classifiers)
        {
            ClassificationResult? result;
            try
            {
                result = await classifier.ClassifyAsync(context, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                continue; // a broken classifier (e.g. LLM offline) must never block a download
            }

            if (result is null) continue;
            if (result.Confidence >= ConfidenceThreshold) return result;
            if (best is null || result.Confidence > best.Confidence) best = result;
        }
        return best ?? new ClassificationResult("Other", "Other", 0, "default");
    }
}
