using QsrPriceBenchmarks.Core.Util;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

[Trait("Category", "Unit")]
public sealed class ConsoleColouriserTests
{
    // ANSI: dim + bright cyan, then reset (matches the Python prototype's _dim).
    private const string DimCyanOpen = "\u001b[2;96m";
    private const string Reset = "\u001b[0m";

    [Fact(DisplayName = "A marked province/district name renders in dim cyan")]
    public void MarkedValue_IsDimCyan()
    {
        ConsoleColouriser.Enabled = true;

        var line = $"  [1/79] Province: {LogMarkup.Value("arnavutkoy")}";
        var outp = ConsoleColouriser.Colourise(line);

        Assert.Contains($"{DimCyanOpen}arnavutkoy{Reset}", outp);
        // The raw markers must never reach the terminal.
        Assert.DoesNotContain(LogMarkup.ValueStart.ToString(), outp);
        Assert.DoesNotContain(LogMarkup.ValueEnd.ToString(), outp);
    }

    [Fact(DisplayName = "District sub-line dims the name but keeps the ↳ arrow cyan")]
    public void DistrictLine_DimsName()
    {
        ConsoleColouriser.Enabled = true;

        var outp = ConsoleColouriser.Colourise($"    ↳ {LogMarkup.Value("kadikoy")}");

        Assert.Contains($"{DimCyanOpen}kadikoy{Reset}", outp);
        Assert.Contains("\u001b[96m↳", outp); // arrow stays light cyan
    }

    [Fact(DisplayName = "With colour disabled, markers are stripped to plain text")]
    public void Disabled_StripsMarkers()
    {
        try
        {
            ConsoleColouriser.Enabled = false;
            var outp = ConsoleColouriser.Colourise($"Province: {LogMarkup.Value("izmir")}");

            Assert.Equal("Province: izmir", outp);
        }
        finally
        {
            ConsoleColouriser.Enabled = true; // restore shared static for other tests
        }
    }
}
