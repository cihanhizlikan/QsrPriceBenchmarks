namespace QsrPriceBenchmarks.Core.Services;

/// <summary>
/// A numeric progress update for a long-running pipeline phase (e.g. Step 3
/// geocoding). Separate from the textual <see cref="System.IProgress{T}"/> log
/// channel so the UI can drive a progress bar without parsing log strings.
/// <paramref name="Total"/> is 0 when no determinate work is in progress.
/// </summary>
public readonly record struct StepProgress(string Phase, int Done, int Total);
