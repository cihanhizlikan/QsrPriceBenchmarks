namespace QsrPriceBenchmarks.Cli.Cli;

public sealed class CliOptions
{
    public string Db { get; set; } = "QsrPriceBenchmarks.sqlite";
    public string? Chain { get; set; }
    public bool ForceCrawl { get; set; }
    public bool Geocode { get; set; }
    public bool GeocodeRematch { get; set; }
    public bool Export { get; set; }
    public long? ExportSince { get; set; }
    public long? DeleteScrapeRun { get; set; }
    public long? DeleteScrapeRunsBeforeId { get; set; }
    public bool ListScrapeRuns { get; set; }
    public bool Help { get; set; }

    public bool IsMaintenance =>
        ListScrapeRuns || DeleteScrapeRun is not null || DeleteScrapeRunsBeforeId is not null;

    public bool IsSelective => ForceCrawl || Geocode || GeocodeRematch || Export || ExportSince is not null;
}

public static class CliParser
{
    /// <summary>
    /// Parse argv into a <see cref="CliOptions"/>. Throws
    /// <see cref="CliArgumentException"/> with a human-readable message on
    /// any invalid combination — mirrors argparse.error()'s exit-with-message
    /// behaviour from the Python prototype.
    /// </summary>
    public static CliOptions Parse(string[] args)
    {
        var opts = new CliOptions();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h":
                case "--help":
                    opts.Help = true;
                    break;
                case "--db":
                    opts.Db = RequireValue(args, ref i, "--db");
                    break;
                case "--chain":
                    opts.Chain = RequireValue(args, ref i, "--chain");
                    break;
                case "--force-crawl":
                    opts.ForceCrawl = true;
                    break;
                case "--geocode":
                    opts.Geocode = true;
                    break;
                case "--geocode-rematch":
                    opts.GeocodeRematch = true;
                    break;
                case "--export":
                    opts.Export = true;
                    break;
                case "--export-since":
                    opts.ExportSince = RequireLong(args, ref i, "--export-since");
                    break;
                case "--delete-scrape-run":
                    opts.DeleteScrapeRun = RequireLong(args, ref i, "--delete-scrape-run");
                    break;
                case "--delete-scrape-runs-before-id":
                    opts.DeleteScrapeRunsBeforeId = RequireLong(args, ref i, "--delete-scrape-runs-before-id");
                    break;
                case "--list-scrape-runs":
                    opts.ListScrapeRuns = true;
                    break;
                default:
                    throw new CliArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (opts.Help)
            return opts;

        if (!opts.IsMaintenance && opts.Chain is null)
        {
            throw new CliArgumentException(
                "--chain QSR_SLUG is required unless using --list-scrape-runs, " +
                "--delete-scrape-run, or --delete-scrape-runs-before-id");
        }

        if (opts.Geocode && opts.GeocodeRematch)
            throw new CliArgumentException("--geocode and --geocode-rematch are mutually exclusive");

        if (opts.Export && opts.ExportSince is not null)
        {
            throw new CliArgumentException(
                "--export and --export-since are mutually exclusive; " +
                "use --export-since RUN_ID to export from a specific run");
        }

        return opts;
    }

    private static string RequireValue(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length)
            throw new CliArgumentException($"{flag} requires a value");
        return args[++i];
    }

    private static long RequireLong(string[] args, ref int i, string flag)
    {
        var raw = RequireValue(args, ref i, flag);
        if (!long.TryParse(raw, out var value))
            throw new CliArgumentException($"{flag} requires an integer, got: {raw}");
        return value;
    }
}

public sealed class CliArgumentException(string message) : Exception(message);
