using QsrPriceBenchmarks.Core.Models;

namespace QsrPriceBenchmarks.Ui.ViewModels;

/// <summary>Where a scrape run stands, as the Scrape Runs tab filters and sorts it.</summary>
public enum RunState
{
    /// <summary>Unfinished and being scraped right now (its <c>RunLock</c> is held, here or in another window).</summary>
    Running,

    /// <summary>Unfinished and nobody scraping it: it errored or its process was killed. Resume shows only then.</summary>
    Unfinished,

    /// <summary>Completed (FINISHED_AT present). Export and Delete show only then.</summary>
    Finished,
}

/// <summary>
/// One row of the Scrape Runs grid. <c>isClaimed</c> says whether some window — this one or another — holds the
/// run's lock (<c>RunLock</c>) right now, i.e. is scraping it.
/// </summary>
public sealed class ScrapeRunViewModel(ScrapeRunRow row, string qsrName, int snapshotCount, bool isClaimed)
{
    public long Id { get; } = row.Id;
    public string QsrName { get; } = qsrName;
    public string StartedAt { get; } = row.StartedAt;
    public string FinishedAt { get; } = row.FinishedAt ?? (isClaimed ? "(running)" : "(unfinished)");
    public int SnapshotCount { get; } = snapshotCount;

    public RunState State { get; } = row.FinishedAt is not null ? RunState.Finished
        : isClaimed ? RunState.Running
        : RunState.Unfinished;

    public bool IsFinished
    {
        get
        {
            return State == RunState.Finished;
        }
    }

    public bool IsErrored
    {
        get
        {
            return State == RunState.Unfinished;
        }
    }
}
