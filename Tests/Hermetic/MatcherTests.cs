using QsrPriceBenchmarks.Core.Scraping;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

[Trait("Category", "Unit")]
public sealed class MatcherTests
{
    [Fact(DisplayName = "Near-identical coords + text -> confident one-to-one match")]
    public void CloseCoords_Matches()
    {
        var pls = new[] { new MatchCandidate(1, "kadikoy-bk", "Caferaga Mah No 1", 40.0000, 29.0000) };
        var locs = new[] { new MatchCandidate(10, "kadikoy-bk", "Caferaga Mah No 1", 40.0001, 29.0001) };

        var r = Matcher.Match(pls, locs).Single(x => x.PlatformLocationId == 1);

        Assert.Equal(10, r.LocationId);
        Assert.False(r.Ambiguous);
        Assert.True(r.Score >= 0.35);
    }

    [Fact(DisplayName = "Far apart with no text overlap -> unmatched")]
    public void FarApart_NoMatch()
    {
        var pls = new[] { new MatchCandidate(1, "aaa-bbb", "Foo Sok No 1", 40.0, 29.0) };
        var locs = new[] { new MatchCandidate(10, "xxx-yyy", "Bar Cad No 2", 41.0, 30.0) }; // ~140 km

        var r = Matcher.Match(pls, locs).Single();

        Assert.Null(r.LocationId);
        Assert.False(r.Ambiguous);
    }

    [Fact(DisplayName = "Two equally-good candidates flag the PL as ambiguous")]
    public void TwoEqualCandidates_Ambiguous()
    {
        var pls = new[] { new MatchCandidate(1, "kadikoy", "Caferaga Mah No 1", 40.0, 29.0) };
        var locs = new[]
        {
            new MatchCandidate(10, "kadikoy", "Caferaga Mah No 1", 40.0001, 29.0001),
            new MatchCandidate(11, "kadikoy", "Caferaga Mah No 1", 40.0001, 29.0001),
        };

        var r = Matcher.Match(pls, locs).Single(x => x.PlatformLocationId == 1);

        // Matcher records the winning LocationId but marks it ambiguous; the
        // caller is responsible for NOT persisting an ambiguous match.
        Assert.True(r.Ambiguous);
    }

    [Fact(DisplayName = "Text-only matching works when neither side has coords")]
    public void TextOnly_Matches()
    {
        var pls = new[] { new MatchCandidate(1, "kadikoy-merkez", "Caferaga Mah Moda Cad No 5 Kadikoy", null, null) };
        var locs = new[] { new MatchCandidate(10, "kadikoy-merkez", "Caferaga Mah Moda Cad No 5 Kadikoy", null, null) };

        var r = Matcher.Match(pls, locs).Single();

        Assert.Equal(10, r.LocationId);
        Assert.False(r.Ambiguous);
    }
}
