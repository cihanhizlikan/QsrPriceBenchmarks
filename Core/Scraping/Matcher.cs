namespace QsrPriceBenchmarks.Core.Scraping;

/// <summary>One candidate to match against: an ID plus optional geo/text signals.</summary>
public sealed record MatchCandidate(
    long Id, string Slug, string? Address, double? Latitude, double? Longitude);

/// <summary>A confirmed or ambiguous match result for one platform location.</summary>
public sealed record MatchResult(
    long PlatformLocationId, long? LocationId, double Score, bool Ambiguous);

/// <summary>
/// Matches PLATFORM_LOCATIONS rows to LOCATIONS rows for the same chain.
///
/// Scoring (mirrors the Python prototype):
///   With coordinates on both sides:
///     score = 0.50 * geoScore + 0.15 * slugJaccard + 0.35 * addressOverlap
///     geoScore = exp(-distanceMeters / 500), 0 beyond a 2000m cutoff
///   Without coordinates on either side:
///     score = 0.25 * slugJaccard + 0.75 * addressOverlap, plus a small
///     bonus when both sides share a province-name token.
///
/// Acceptance: best score >= 0.35 AND beats the second-best candidate by
/// >= 0.08 (otherwise flagged ambiguous for manual review). Matching is
/// greedy and one-to-one: pairs are sorted by score globally and assigned
/// in descending order, skipping either side once it's already taken.
/// </summary>
public static class Matcher
{
    private const double AcceptThreshold = 0.35;
    private const double AmbiguityGap = 0.08;
    private const double GeoCutoffMeters = 2000.0;
    private const double GeoDecayMeters = 500.0;

    public static List<MatchResult> Match(
        IReadOnlyList<MatchCandidate> platformLocations,
        IReadOnlyList<MatchCandidate> locations)
    {
        // Compute every candidate pair's score, plus each PL's top-2 scores
        // (needed for the ambiguity gap check).
        var allPairs = new List<(long PlId, long LocId, double Score)>();

        foreach (var pl in platformLocations)
        {
            foreach (var loc in locations)
            {
                var score = ScorePair(pl, loc);
                if (score > 0)
                    allPairs.Add((pl.Id, loc.Id, score));
            }
        }

        // Determine ambiguity per PL: top score must beat runner-up by the gap.
        var byPl = allPairs.GroupBy(p => p.PlId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Score).ToList());

        var ambiguousPls = new HashSet<long>();
        foreach (var (plId, ranked) in byPl)
        {
            if (ranked.Count >= 2 && ranked[0].Score - ranked[1].Score < AmbiguityGap
                && ranked[0].Score >= AcceptThreshold)
            {
                ambiguousPls.Add(plId);
            }
        }

        // Greedy one-to-one assignment over pairs meeting the threshold,
        // highest score first, skipping either side once claimed.
        var usedPl = new HashSet<long>();
        var usedLoc = new HashSet<long>();
        var results = new Dictionary<long, MatchResult>();

        foreach (var pair in allPairs
                     .Where(p => p.Score >= AcceptThreshold)
                     .OrderByDescending(p => p.Score))
        {
            if (usedPl.Contains(pair.PlId) || usedLoc.Contains(pair.LocId))
                continue;

            usedPl.Add(pair.PlId);
            usedLoc.Add(pair.LocId);
            results[pair.PlId] = new MatchResult(
                pair.PlId, pair.LocId, pair.Score, ambiguousPls.Contains(pair.PlId));
        }

        // Every PL not assigned: either no candidate met the threshold, or
        // it lost the greedy race to a higher-scoring competitor. Report it
        // as unmatched (LocationId = null) so callers can count it.
        foreach (var pl in platformLocations)
        {
            if (!results.ContainsKey(pl.Id))
                results[pl.Id] = new MatchResult(pl.Id, null, 0, false);
        }

        return results.Values.ToList();
    }

    private static double ScorePair(MatchCandidate pl, MatchCandidate loc)
    {
        var slugScore = JaccardTokens(TokenizeSlug(pl.Slug), TokenizeSlug(loc.Slug));
        var addressScore = AddressOverlap(pl.Address, loc.Address);

        var bothHaveCoords = pl.Latitude is not null && pl.Longitude is not null
                           && loc.Latitude is not null && loc.Longitude is not null;

        if (bothHaveCoords)
        {
            var distance = HaversineMeters(
                pl.Latitude!.Value, pl.Longitude!.Value, loc.Latitude!.Value, loc.Longitude!.Value);
            if (distance > GeoCutoffMeters)
                return 0;

            var geoScore = Math.Exp(-distance / GeoDecayMeters);
            return 0.50 * geoScore + 0.15 * slugScore + 0.35 * addressScore;
        }
        else
        {
            var provinceBonus = SharesProvinceToken(pl.Address, loc.Address) ? 0.05 : 0.0;
            return Math.Min(1.0, 0.25 * slugScore + 0.75 * addressScore + provinceBonus);
        }
    }

    // ── Geo ──────────────────────────────────────────────────────────────────

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusM = 6_371_000.0;
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
                  * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusM * c;
    }

    private static double ToRadians(double deg) => deg * Math.PI / 180.0;

    // ── Slug / address text similarity ──────────────────────────────────────

    private static HashSet<string> TokenizeSlug(string slug) =>
        slug.ToLowerInvariant()
            .Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1) // drop single-char hash fragments / noise
            .ToHashSet();

    private static readonly char[] AddressSeparators = { ' ', ',', '.', '/', '(', ')', ':' };

    private static HashSet<string> TokenizeAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return new HashSet<string>();

        return address.ToLowerInvariant()
            .Split(AddressSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 1)
            .ToHashSet();
    }

    private static double JaccardTokens(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
            return 0;
        var intersection = a.Intersect(b).Count();
        var union = a.Union(b).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static double AddressOverlap(string? addr1, string? addr2)
    {
        var tokensA = TokenizeAddress(addr1);
        var tokensB = TokenizeAddress(addr2);
        if (tokensA.Count == 0 || tokensB.Count == 0)
            return 0;

        // Overlap coefficient (intersection / smaller set size) — more
        // forgiving than Jaccard when one address is much more detailed
        // than the other (common: chain-website address vs. TG's shorter one).
        var intersection = tokensA.Intersect(tokensB).Count();
        var smaller = Math.Min(tokensA.Count, tokensB.Count);
        return smaller == 0 ? 0 : (double)intersection / smaller;
    }

    private static bool SharesProvinceToken(string? addr1, string? addr2)
    {
        var tokensA = TokenizeAddress(addr1);
        var tokensB = TokenizeAddress(addr2);
        return tokensA.Intersect(tokensB).Any();
    }
}
