using QsrPriceBenchmarks.Core.Models;

namespace QsrPriceBenchmarks.Ui.ViewModels;

public sealed class ScrapeRunViewModel(ScrapeRunRow row, string qsrName, int snapshotCount)
{
    public long   Id            { get; } = row.Id;
    public string QsrName       { get; } = qsrName;
    public string StartedAt     { get; } = row.StartedAt;
    public string FinishedAt    { get; } = row.FinishedAt ?? "(unfinished)";
    public bool   IsRunning     { get; } = row.FinishedAt is null;
    public int    SnapshotCount { get; } = snapshotCount;

    /// <summary>True when the run completed (FINISHED_AT present) — Export shows only then.</summary>
    public bool IsFinished => !IsRunning;

    /// <summary>
    /// True when the run was never finalized (FINISHED_AT null). Because every
    /// run is finalized in a finally block, this means it errored/was killed —
    /// Resume shows only then.
    /// </summary>
    public bool IsErrored => IsRunning;
}
