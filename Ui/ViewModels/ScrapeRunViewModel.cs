using QsrPriceBenchmarks.Core.Models;

namespace QsrPriceBenchmarks.Ui.ViewModels;

/// <summary>
/// One row of the Scrape Runs grid. <c>isClaimed</c> says whether some window — this one or another — holds the
/// run's lock (<c>RunLock</c>) right now, i.e. is scraping it.
/// </summary>
public sealed class ScrapeRunViewModel(ScrapeRunRow row, string qsrName, int snapshotCount, bool isClaimed)
{
    private readonly bool _unfinished = row.FinishedAt is null;

    public long Id { get; } = row.Id;
    public string QsrName { get; } = qsrName;
    public string StartedAt { get; } = row.StartedAt;
    public string FinishedAt { get; } = row.FinishedAt ?? (isClaimed ? "(running)" : "(unfinished)");
    public int SnapshotCount { get; } = snapshotCount;

    /// <summary>True when the run completed (FINISHED_AT present) — Export shows only then.</summary>
    public bool IsFinished
    {
        get
        {
            return !_unfinished;
        }
    }

    /// <summary>
    /// True when the run was never finalized and nobody is scraping it: every run is finalized in a finally block
    /// and claimed until then, so this means it errored or its process was killed — Resume shows only then.
    /// </summary>
    public bool IsErrored
    {
        get
        {
            return _unfinished && !isClaimed;
        }
    }
}
