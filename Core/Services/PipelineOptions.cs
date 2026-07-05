namespace QsrPriceBenchmarks.Core.Services;

/// <summary>
/// Encapsulates what the user wants the pipeline to do for one chain.
/// Both the CLI and the UI construct this from their respective input
/// mechanisms and pass it to <see cref="PipelineRunner.RunAsync"/>.
/// Mirrors the flag combinations in CliOptions but as a plain DTO with
/// no string-parsing concerns.
/// </summary>
public sealed class PipelineOptions
{
    public required Models.ChainConfig Chain { get; init; }

    // Step flags — same semantics as CLI flags:
    public bool ForceCrawl      { get; init; }
    public bool Geocode         { get; init; }
    public bool GeocodeRematch  { get; init; }
    public bool Export          { get; init; }
    public long? ExportSince    { get; init; }

    /// <summary>
    /// Optional subset of TG menu tab names to scrape during a full run
    /// (null = every configured tab). Selective runs never reach Step 2, so
    /// this is only consulted on a full run. The CLI leaves it null.
    /// </summary>
    public IReadOnlyCollection<string>? Tabs { get; init; }

    /// <summary>
    /// True when any step flag is set (selective run).
    /// False = normal full run (Steps 1->2->3).
    /// </summary>
    public bool IsSelective =>
        ForceCrawl || Geocode || GeocodeRematch || Export || ExportSince is not null;

    /// <summary>Output folder for Excel files. Defaults to "output" beside the DB.</summary>
    public string OutputDir { get; init; } = "output";
}
