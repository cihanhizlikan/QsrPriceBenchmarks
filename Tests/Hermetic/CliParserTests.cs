using QsrPriceBenchmarks.Cli.Cli;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

[Trait("Category", "Unit")]
public sealed class CliParserTests
{
    [Fact(DisplayName = "Valid: --chain sets the chain slug")]
    public void Valid_Chain()
    {
        var o = CliParser.Parse(new[] { "--chain", "burger-king" });
        Assert.Equal("burger-king", o.Chain);
        Assert.False(o.IsSelective);
        Assert.False(o.IsMaintenance);
    }

    [Fact(DisplayName = "Selective flags + db path parse together")]
    public void Valid_SelectiveWithDb()
    {
        var o = CliParser.Parse(new[] { "--chain", "popeyes", "--db", "x.sqlite", "--geocode", "--export-since", "7" });
        Assert.Equal("x.sqlite", o.Db);
        Assert.True(o.Geocode);
        Assert.Equal(7, o.ExportSince);
        Assert.True(o.IsSelective);
    }

    [Fact(DisplayName = "--help short-circuits without requiring --chain")]
    public void Help_NoChainRequired() =>
        Assert.True(CliParser.Parse(new[] { "--help" }).Help);

    [Fact(DisplayName = "Maintenance commands don't require --chain")]
    public void Maintenance_NoChainRequired() =>
        Assert.True(CliParser.Parse(new[] { "--list-scrape-runs" }).ListScrapeRuns);

    [Fact(DisplayName = "Missing --chain (non-maintenance) throws")]
    public void MissingChain_Throws() =>
        Assert.Throws<CliArgumentException>(() => CliParser.Parse(Array.Empty<string>()));

    [Fact(DisplayName = "--geocode and --geocode-rematch are mutually exclusive")]
    public void GeocodeFlags_MutuallyExclusive() =>
        Assert.Throws<CliArgumentException>(() =>
            CliParser.Parse(new[] { "--chain", "x", "--geocode", "--geocode-rematch" }));

    [Fact(DisplayName = "--export and --export-since are mutually exclusive")]
    public void ExportFlags_MutuallyExclusive() =>
        Assert.Throws<CliArgumentException>(() =>
            CliParser.Parse(new[] { "--chain", "x", "--export", "--export-since", "3" }));

    [Fact(DisplayName = "A flag expecting a value throws when it's missing")]
    public void MissingValue_Throws() =>
        Assert.Throws<CliArgumentException>(() => CliParser.Parse(new[] { "--db" }));

    [Fact(DisplayName = "A flag expecting an integer rejects non-numeric input")]
    public void NonInteger_Throws() =>
        Assert.Throws<CliArgumentException>(() =>
            CliParser.Parse(new[] { "--chain", "x", "--export-since", "abc" }));

    [Fact(DisplayName = "Unknown argument throws")]
    public void Unknown_Throws() =>
        Assert.Throws<CliArgumentException>(() => CliParser.Parse(new[] { "--bogus" }));
}
