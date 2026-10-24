using System.Text.RegularExpressions;
using QsrPriceBenchmarks.Core.Util;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// What the CLI's colouring must do, whatever the palette: a marked value is
/// styled and the style is reset after it, the raw markers never reach the
/// terminal, and with colour off the line is plain text. WHICH colour a value
/// gets is a choice, not a requirement, so no test names one (plan WP-T2).
/// </summary>
[Trait("Category", "Unit")]
public sealed class ConsoleColouriserTests
{
    // Any SGR sequence ("ESC [ params m"), and the reset that must follow a styled value.
    private const string Sgr = @"\u001b\[[0-9;]+m";
    private const string Reset = "\u001b[0m";

    [Fact(DisplayName = "A marked value is styled, reset after, and its markers are stripped")]
    public void MarkedValue_IsStyledAndMarkersStripped()
    {
        ConsoleColouriser.Enabled = true;

        string outp = ConsoleColouriser.Colourise($"  [1/79] Province: {LogMarkup.Value("arnavutkoy")}");

        Assert.Matches($"{Sgr}arnavutkoy{Regex.Escape(Reset)}", outp);
        Assert.DoesNotContain(LogMarkup.ValueStart.ToString(), outp);
        Assert.DoesNotContain(LogMarkup.ValueEnd.ToString(), outp);
    }

    [Fact(DisplayName = "On a district sub-line the marked name is styled apart from the ↳ arrow")]
    public void DistrictLine_NameStyledApartFromArrow()
    {
        ConsoleColouriser.Enabled = true;

        string outp = ConsoleColouriser.Colourise($"    ↳ {LogMarkup.Value("kadikoy")}");

        Match arrow = Regex.Match(outp, $"({Sgr})↳");
        Match name = Regex.Match(outp, $"({Sgr})kadikoy{Regex.Escape(Reset)}");
        Assert.True(arrow.Success, "the ↳ arrow must be styled");
        Assert.True(name.Success, "the marked name must be styled and reset");
        Assert.NotEqual(arrow.Groups[1].Value, name.Groups[1].Value);
    }

    [Fact(DisplayName = "With colour disabled, markers are stripped to plain text")]
    public void Disabled_StripsMarkers()
    {
        try
        {
            ConsoleColouriser.Enabled = false;
            string outp = ConsoleColouriser.Colourise($"Province: {LogMarkup.Value("izmir")}");

            Assert.Equal("Province: izmir", outp);
        }
        finally
        {
            ConsoleColouriser.Enabled = true; // restore shared static for other tests
        }
    }
}
