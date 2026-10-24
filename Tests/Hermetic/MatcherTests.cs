using QsrPriceBenchmarks.Core.Scraping;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

[Trait("Category", "Unit")]
public sealed class MatcherTests
{
    [Fact(DisplayName = "Near-identical coords + text -> confident one-to-one match")]
    public void CloseCoords_Matches()
    {
        MatchCandidate[] pls = [new MatchCandidate(1, "kadikoy-bk", "Caferaga Mah No 1", 40.0000, 29.0000)];
        MatchCandidate[] locs = [new MatchCandidate(10, "kadikoy-bk", "Caferaga Mah No 1", 40.0001, 29.0001)];

        MatchResult r = Matcher.Match(pls, locs).Single(x => x.PlatformLocationId == 1);

        Assert.Equal(10, r.LocationId);
        Assert.False(r.Ambiguous);
        Assert.True(r.Score >= 0.35);
    }

    [Fact(DisplayName = "Far apart with no text overlap -> unmatched")]
    public void FarApart_NoMatch()
    {
        MatchCandidate[] pls = [new MatchCandidate(1, "aaa-bbb", "Foo Sok No 1", 40.0, 29.0)];
        MatchCandidate[] locs = [new MatchCandidate(10, "xxx-yyy", "Bar Cad No 2", 41.0, 30.0)]; // ~140 km

        MatchResult r = Matcher.Match(pls, locs).Single();

        Assert.Null(r.LocationId);
        Assert.False(r.Ambiguous);
    }

    [Fact(DisplayName = "Two equally-good candidates flag the PL as ambiguous")]
    public void TwoEqualCandidates_Ambiguous()
    {
        MatchCandidate[] pls = [new MatchCandidate(1, "kadikoy", "Caferaga Mah No 1", 40.0, 29.0)];
        MatchCandidate[] locs =
        [
            new MatchCandidate(10, "kadikoy", "Caferaga Mah No 1", 40.0001, 29.0001),
            new MatchCandidate(11, "kadikoy", "Caferaga Mah No 1", 40.0001, 29.0001),
        ];

        MatchResult r = Matcher.Match(pls, locs).Single(x => x.PlatformLocationId == 1);

        // Matcher records the winning LocationId but marks it ambiguous; the
        // caller is responsible for NOT persisting an ambiguous match.
        Assert.True(r.Ambiguous);
    }

    [Fact(DisplayName = "Text-only matching works when neither side has coords")]
    public void TextOnly_Matches()
    {
        MatchCandidate[] pls = [new MatchCandidate(1, "kadikoy-merkez", "Caferaga Mah Moda Cad No 5 Kadikoy", null, null)];
        MatchCandidate[] locs = [new MatchCandidate(10, "kadikoy-merkez", "Caferaga Mah Moda Cad No 5 Kadikoy", null, null)];

        MatchResult r = Matcher.Match(pls, locs).Single();

        Assert.Equal(10, r.LocationId);
        Assert.False(r.Ambiguous);
    }

    // ── v1.2 algorithm behaviours ─────────────────────────────────────────────

    private static MatchResult ResultFor(List<MatchResult> results, long plId)
    {
        return Assert.Single(results, x => x.PlatformLocationId == plId);
    }

    [Fact(DisplayName = "Turkish folding: ASCII slug tokens meet Turkish address tokens")]
    public void TurkishFolding_AsciiSlugMeetsTurkishAddress()
    {
        // TG slug is ASCII ("kadikoy", "moda"); the chain site's address uses
        // Turkish letters. Without folding these token sets never intersect.
        List<MatchCandidate> pls = [new MatchCandidate(1, "burger-king-kadikoy-moda", "Caferağa Mah. Moda Cad. No:81 Kadıköy İstanbul", null, null),];
        List<MatchCandidate> locs =
        [
            new MatchCandidate(101, "moda", "Caferağa Mahallesi Moda Caddesi No: 81/A Kadıköy/İstanbul", null, null),
            new MatchCandidate(102, "besiktas-carsi", "Sinanpaşa Mahallesi Ortabahçe Caddesi No:2/B Beşiktaş/İstanbul", null, null),
        ];

        MatchResult result = ResultFor(Matcher.Match(pls, locs, "Burger King"), 1);
        Assert.Equal(101, result.LocationId);
        Assert.False(result.Ambiguous);
    }

    [Fact(DisplayName = "IDF weighting: shared city/street tokens don't tie; slug branch name decides")]
    public void IdfWeighting_SharedCityTokensDoNotMisleadOrTie()
    {
        // Both candidates share the city and the famous street with the PL;
        // the branch-name slug tokens ("bagdat" + "caddesi", kept whole in
        // slugs) must decide it in favour of the like-named branch.
        List<MatchCandidate> pls = [new MatchCandidate(1, "burger-king-bagdat-caddesi", "Bağdat Cd. Kadıköy", null, null),];
        List<MatchCandidate> locs =
        [
            new MatchCandidate(101, "bagdat-caddesi", "Caddebostan Mahallesi Bağdat Caddesi No:302 Kadıköy/İstanbul", null, null),
            new MatchCandidate(102, "suadiye", "Suadiye Mahallesi Bağdat Caddesi No:398 Kadıköy/İstanbul", null, null),
        ];

        MatchResult result = ResultFor(Matcher.Match(pls, locs, "Burger King"), 1);
        Assert.Equal(101, result.LocationId);
        Assert.False(result.Ambiguous);
    }

    [Fact(DisplayName = "Slug-only PL (no address) matches through merged tokens")]
    public void SlugOnlyPlatformLocation_MatchesThroughMergedTokens()
    {
        // TG rows frequently carry no address at all; the slug's neighbourhood
        // token has to meet the chain address on its own.
        List<MatchCandidate> pls = [new MatchCandidate(1, "burger-king-suadiye", null, null, null),];
        List<MatchCandidate> locs =
        [
            new MatchCandidate(101, "suadiye", "Suadiye Mahallesi Bağdat Caddesi No:398 Kadıköy/İstanbul", null, null),
            new MatchCandidate(102, "moda", "Caferağa Mahallesi Moda Caddesi No:81 Kadıköy/İstanbul", null, null),
        ];

        MatchResult result = ResultFor(Matcher.Match(pls, locs, "Burger King"), 1);
        Assert.Equal(101, result.LocationId);
    }

    [Fact(DisplayName = "Geo lock-in: nearest isolated candidate wins despite useless text")]
    public void GeoLockIn_NearestIsolatedCandidateWins()
    {
        // ~50 m apart, next candidate kilometres away, addresses share nothing:
        // geography alone must decide it.
        List<MatchCandidate> pls = [new MatchCandidate(1, "burger-king-akasya-avm", "Akasya AVM", 41.0022, 29.0555),];
        List<MatchCandidate> locs =
        [
            new MatchCandidate(101, "acibadem", "Acıbadem Mahallesi Çeçen Sokak Üsküdar", 41.0025, 29.0560),
            new MatchCandidate(102, "uskudar-meydan", "Mimar Sinan Mahallesi Hakimiyeti Milliye Caddesi Üsküdar", 41.0250, 29.0150),
        ];

        MatchResult result = ResultFor(Matcher.Match(pls, locs, "Burger King"), 1);
        Assert.Equal(101, result.LocationId);
        Assert.False(result.Ambiguous);
        Assert.True(result.Score >= 0.95);
    }

    [Fact(DisplayName = "Bad geocode pin: strong address agreement still matches")]
    public void BadGeocodePin_StrongAddressAgreementStillMatches()
    {
        // The PL was pinned to the wrong city (hundreds of km off), but the
        // addresses agree almost token for token. The old hard cutoff discarded
        // such pairs outright; the penalised text fallback must recover it.
        List<MatchCandidate> pls = [new MatchCandidate(1, "burger-king-alsancak", "Kıbrıs Şehitleri Cad. No:140 Alsancak Konak İzmir", 39.9, 32.8),];
        List<MatchCandidate> locs = [new MatchCandidate(101, "alsancak", "Kıbrıs Şehitleri Caddesi No:140/A Alsancak Konak/İzmir", 38.437, 27.142),];

        MatchResult result = ResultFor(Matcher.Match(pls, locs, "Burger King"), 1);
        Assert.Equal(101, result.LocationId);
    }

    [Fact(DisplayName = "True no-counterpart PL stays unmatched")]
    public void NoCounterpart_StaysUnmatched()
    {
        // A branch that exists only on the platform (e.g. a brand-new mall)
        // must not be forced onto some unrelated location.
        List<MatchCandidate> pls = [new MatchCandidate(1, "burger-king-hilltown-avm", "Hilltown AVM Küçükyalı Maltepe", null, null),];
        List<MatchCandidate> locs =
        [
            new MatchCandidate(101, "moda", "Caferağa Mahallesi Moda Caddesi No:81 Kadıköy/İstanbul", null, null),
            new MatchCandidate(102, "besiktas-carsi", "Sinanpaşa Mahallesi Ortabahçe Caddesi No:2/B Beşiktaş/İstanbul", null, null),
        ];

        MatchResult result = ResultFor(Matcher.Match(pls, locs, "Burger King"), 1);
        Assert.Null(result.LocationId);
    }

    [Fact(DisplayName = "Greedy one-to-one: each location claimed exactly once")]
    public void GreedyOneToOne_EachLocationClaimedOnce()
    {
        List<MatchCandidate> pls =
        [
            new MatchCandidate(1, "burger-king-moda", "Moda Caddesi No:81 Kadıköy İstanbul", null, null),
            new MatchCandidate(2, "burger-king-suadiye", "Suadiye Bağdat Caddesi No:398 Kadıköy İstanbul", null, null),
        ];
        List<MatchCandidate> locs =
        [
            new MatchCandidate(101, "moda", "Caferağa Mahallesi Moda Caddesi No:81 Kadıköy/İstanbul", null, null),
            new MatchCandidate(102, "suadiye", "Suadiye Mahallesi Bağdat Caddesi No:398 Kadıköy/İstanbul", null, null),
        ];

        List<MatchResult> results = Matcher.Match(pls, locs, "Burger King");
        Assert.Equal(101, ResultFor(results, 1).LocationId);
        Assert.Equal(102, ResultFor(results, 2).LocationId);
    }
}
