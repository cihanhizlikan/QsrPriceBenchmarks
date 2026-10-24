using System.Text;

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
/// Text signal: each candidate's slug and address tokens are merged into one
/// set, Turkish-folded (ç→c, ğ→g, ı/İ→i, ö→o, ş→s, ü→u) so the platform's
/// ASCII slugs ("kadikoy") meet the chain site's Turkish addresses ("Kadıköy"),
/// and address-structure words (mahalle/cadde/sokak/no/…) are dropped from the
/// address side (slug tokens are kept whole — see SignalTokens). Tokens
/// are weighted by rarity (IDF across the chain's candidates), so brand tokens
/// that appear in every platform slug and city names shared by half the branches
/// carry almost no weight, while neighbourhood names, street names, and street
/// numbers dominate. The pair score is the IDF-weighted overlap coefficient.
///
/// Geo signal, when both sides have coordinates:
///   score = 0.5 * exp(-distanceMeters / 500) + 0.5 * textScore
/// Beyond a 2000 m cutoff the pair is NOT discarded (one bad geocode pin used
/// to kill otherwise perfect matches) — it falls back to the text score at a
/// 0.7 distrust penalty.
///
/// Geo lock-in: a platform location whose nearest same-chain candidate is
/// within 200 m, with no second candidate closer than 600 m, is matched on
/// geography alone (score pinned to ≥ 0.95) — two branches of one chain
/// practically never stand that close and that isolated.
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
    private const double BeyondCutoffTextPenalty = 0.7;
    private const double GeoLockInMeters = 200.0;
    private const double GeoLockInIsolationMeters = 600.0;
    private const double GeoLockInScore = 0.95;

    /// <summary>
    /// Turkish address-structure words (in folded form). They appear in nearly
    /// every address, so they say nothing about WHICH address this is — left in,
    /// they inflate the overlap of wrong pairs and drown out the informative
    /// tokens (neighbourhood, street, number) that decide a match.
    /// </summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "mah", "mahalle", "mahallesi", "mh",
        "cad", "cadde", "caddesi", "cd",
        "sok", "sokak", "sokagi", "sk",
        "bulvar", "bulvari", "blv", "bul",
        "no", "blok", "kat", "daire", "apt", "apartman", "apartmani",
        "site", "sitesi", "plaza", "avm", "sube", "subesi",
    };

    public static List<MatchResult> Match(
        IReadOnlyList<MatchCandidate> platformLocations,
        IReadOnlyList<MatchCandidate> locations,
        string? chainName = null)
    {
        // The chain's own name tokens ("burger", "king") sit in every platform
        // slug and say nothing about WHICH branch it is. In a large corpus IDF
        // already demotes them, but in a small one (a late-run rematch of a
        // handful of leftovers) they masquerade as rare and drown the real
        // signal — so when the caller names the chain, they're excluded outright.
        HashSet<string> brandTokens = BrandTokens(chainName);

        // Token sets are computed once per candidate; IDF weights come from the
        // combined corpus (every candidate on both sides counts as a document).
        Dictionary<long, HashSet<string>> plTokens = platformLocations.ToDictionary(
            c => c.Id, c => SignalTokens(c, brandTokens));
        Dictionary<long, HashSet<string>> locTokens = locations.ToDictionary(
            c => c.Id, c => SignalTokens(c, brandTokens));
        Dictionary<string, double> idf = BuildIdf([.. plTokens.Values, .. locTokens.Values]);

        // Geo lock-in pre-pass: platform locations whose nearest candidate is
        // unambiguously THE one by geography alone.
        Dictionary<long, long> lockedIn = FindGeoLockIns(platformLocations, locations);

        List<(long PlId, long LocId, double Score)> allPairs = [];
        foreach (MatchCandidate pl in platformLocations)
        {
            foreach (MatchCandidate loc in locations)
            {
                double score = ScorePair(pl, loc, plTokens[pl.Id], locTokens[loc.Id], idf);
                if (lockedIn.TryGetValue(pl.Id, out long lockedLocId) && lockedLocId == loc.Id)
                {
                    score = Math.Max(score, GeoLockInScore);
                }
                if (score > 0)
                {
                    allPairs.Add((pl.Id, loc.Id, score));
                }
            }
        }

        // Determine ambiguity per PL: top score must beat runner-up by the gap.
        Dictionary<long, List<(long PlId, long LocId, double Score)>> byPl = allPairs
            .GroupBy(p => p.PlId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(p => p.Score).ToList());

        HashSet<long> ambiguousPls = [];
        foreach ((long plId, List<(long PlId, long LocId, double Score)> ranked) in byPl)
        {
            if (ranked.Count >= 2 && ranked[0].Score - ranked[1].Score < AmbiguityGap
                && ranked[0].Score >= AcceptThreshold)
            {
                ambiguousPls.Add(plId);
            }
        }

        // Greedy one-to-one assignment over pairs meeting the threshold,
        // highest score first, skipping either side once claimed.
        HashSet<long> usedPl = [];
        HashSet<long> usedLoc = [];
        Dictionary<long, MatchResult> results = [];

        foreach ((long PlId, long LocId, double Score) pair in allPairs
                     .Where(p => p.Score >= AcceptThreshold)
                     .OrderByDescending(p => p.Score))
        {
            if (usedPl.Contains(pair.PlId) || usedLoc.Contains(pair.LocId))
            {
                continue;
            }

            usedPl.Add(pair.PlId);
            usedLoc.Add(pair.LocId);
            results[pair.PlId] = new MatchResult(
                pair.PlId, pair.LocId, pair.Score, ambiguousPls.Contains(pair.PlId));
        }

        // Every PL not assigned: either no candidate met the threshold, or
        // it lost the greedy race to a higher-scoring competitor. Report it
        // as unmatched (LocationId = null) so callers can count it.
        foreach (MatchCandidate pl in platformLocations)
        {
            if (!results.ContainsKey(pl.Id))
            {
                results[pl.Id] = new MatchResult(pl.Id, null, 0, false);
            }
        }

        return [.. results.Values];
    }

    private static double ScorePair(
        MatchCandidate pl, MatchCandidate loc,
        HashSet<string> plTokens, HashSet<string> locTokens,
        Dictionary<string, double> idf)
    {
        double textScore = WeightedOverlap(plTokens, locTokens, idf);

        bool bothHaveCoords = pl.Latitude is not null && pl.Longitude is not null
                           && loc.Latitude is not null && loc.Longitude is not null;
        if (!bothHaveCoords)
        {
            return textScore;
        }

        double distance = HaversineMeters(
            pl.Latitude!.Value, pl.Longitude!.Value, loc.Latitude!.Value, loc.Longitude!.Value);
        if (distance > GeoCutoffMeters)
        {
            // Coordinates disagree — but one of the two pins may simply be a bad
            // geocode. Distrust the pair rather than discarding it: strong text
            // agreement can still carry it over the accept threshold.
            return BeyondCutoffTextPenalty * textScore;
        }

        double geoScore = Math.Exp(-distance / GeoDecayMeters);
        return 0.5 * geoScore + 0.5 * textScore;
    }

    /// <summary>
    /// Platform locations decided by geography alone: nearest candidate within
    /// <see cref="GeoLockInMeters"/> and no other candidate within
    /// <see cref="GeoLockInIsolationMeters"/>. Returns PL id → LOC id.
    /// </summary>
    private static Dictionary<long, long> FindGeoLockIns(
        IReadOnlyList<MatchCandidate> platformLocations,
        IReadOnlyList<MatchCandidate> locations)
    {
        Dictionary<long, long> locked = [];
        List<MatchCandidate> geoLocs = [.. locations.Where(l => l.Latitude is not null && l.Longitude is not null)];
        if (geoLocs.Count == 0)
        {
            return locked;
        }

        foreach (MatchCandidate pl in platformLocations)
        {
            if (pl.Latitude is null || pl.Longitude is null)
            {
                continue;
            }

            long bestId = 0;
            double best = double.MaxValue;
            double secondBest = double.MaxValue;
            foreach (MatchCandidate loc in geoLocs)
            {
                double d = HaversineMeters(
                    pl.Latitude.Value, pl.Longitude.Value, loc.Latitude!.Value, loc.Longitude!.Value);
                if (d < best)
                {
                    secondBest = best;
                    best = d;
                    bestId = loc.Id;
                }
                else if (d < secondBest)
                {
                    secondBest = d;
                }
            }

            if (best <= GeoLockInMeters && secondBest >= GeoLockInIsolationMeters)
            {
                locked[pl.Id] = bestId;
            }
        }
        return locked;
    }

    // ── Geo ──────────────────────────────────────────────────────────────────

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusM = 6_371_000.0;
        double dLat = ToRadians(lat2 - lat1);
        double dLon = ToRadians(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
                   * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusM * c;
    }

    private static double ToRadians(double deg)
    {
        return deg * Math.PI / 180.0;
    }

    // ── Text signal ──────────────────────────────────────────────────────────

    /// <summary>
    /// Fold Turkish letters to their ASCII neighbours and lowercase. Done
    /// character-by-character BEFORE any lowercasing: 'İ'.ToLowerInvariant()
    /// yields "i" + a combining dot (U+0307), which never equals a plain "i" —
    /// the single biggest reason slug tokens ("kadikoy") failed to meet their
    /// address counterparts ("Kadıköy").
    /// </summary>
    private static string Fold(string text)
    {
        StringBuilder sb = new(text.Length);
        foreach (char c in text)
        {
            char folded = c switch
            {
                'ç' or 'Ç' => 'c',
                'ğ' or 'Ğ' => 'g',
                'ı' or 'İ' => 'i',
                'ö' or 'Ö' => 'o',
                'ş' or 'Ş' => 's',
                'ü' or 'Ü' => 'u',
                _ => char.ToLowerInvariant(c),
            };
            sb.Append(folded);
        }
        return sb.ToString();
    }

    private static readonly char[] SlugSeparators = ['-'];

    private static readonly char[] AddressSeparators =
        [' ', ',', '.', '/', '(', ')', ':', ';', '-', '"', '\t'];

    /// <summary>
    /// One combined token set per candidate: slug tokens and address tokens,
    /// folded; stopwords are removed from ADDRESS tokens only. Slug tokens are
    /// exempt on purpose — a slug is a curated identifier, so a structure word
    /// inside it is part of the branch's NAME ("bagdat-caddesi") and is exactly
    /// the discriminator that separates it from other branches on the same
    /// street. Merging the two sets lets a slug's neighbourhood token confirm
    /// the other side's address (and vice versa) — the platform's slug is often
    /// the only place its neighbourhood appears.
    /// </summary>
    private static HashSet<string> SignalTokens(MatchCandidate candidate, HashSet<string> brandTokens)
    {
        HashSet<string> tokens = new(StringComparer.Ordinal);
        foreach (string token in Fold(candidate.Slug).Split(
                     SlugSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length > 1)
            {
                tokens.Add(token);
            }
        }
        if (!string.IsNullOrWhiteSpace(candidate.Address))
        {
            foreach (string token in Fold(candidate.Address).Split(
                         AddressSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token.Length > 1 && !Stopwords.Contains(token))
                {
                    tokens.Add(token);
                }
            }
        }
        tokens.ExceptWith(brandTokens);
        return tokens;
    }

    /// <summary>Folded tokens of the chain's display name ("Burger King" → burger, king).</summary>
    private static HashSet<string> BrandTokens(string? chainName)
    {
        HashSet<string> tokens = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(chainName))
        {
            return tokens;
        }
        StringBuilder current = new();
        foreach (char c in Fold(chainName))
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                if (current.Length > 1)
                {
                    tokens.Add(current.ToString());
                }
                current.Clear();
            }
        }
        if (current.Length > 1)
        {
            tokens.Add(current.ToString());
        }
        return tokens;
    }

    /// <summary>
    /// Inverse document frequency per token over the chain's candidates. Brand
    /// tokens sitting in every platform slug and city names shared by half the
    /// branches end up with near-zero weight; a street name or number unique to
    /// one branch ends up with the highest — no hand-tuned lists needed.
    /// </summary>
    private static Dictionary<string, double> BuildIdf(IReadOnlyList<HashSet<string>> documents)
    {
        Dictionary<string, int> df = new(StringComparer.Ordinal);
        foreach (HashSet<string> doc in documents)
        {
            foreach (string token in doc)
            {
                df[token] = df.TryGetValue(token, out int n) ? n + 1 : 1;
            }
        }

        Dictionary<string, double> idf = new(df.Count, StringComparer.Ordinal);
        double total = documents.Count;
        foreach ((string token, int count) in df)
        {
            // The 1 + … smoothing keeps the weight of a token that appears in
            // EVERY document above zero — without it, two identical candidates
            // in a small corpus score 0 because all their tokens "carry no
            // information", which is degenerate for matching purposes.
            idf[token] = Math.Log(1.0 + (total + 1.0) / (count + 1.0));
        }
        return idf;
    }

    /// <summary>
    /// IDF-weighted overlap coefficient: shared-token weight divided by the
    /// lighter side's total weight. Overlap (not Jaccard) keeps the score fair
    /// when one address is far more detailed than the other — the norm between
    /// a chain site's full address and the platform's short one.
    /// </summary>
    private static double WeightedOverlap(
        HashSet<string> a, HashSet<string> b, Dictionary<string, double> idf)
    {
        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        double Weight(HashSet<string> tokens)
        {
            double sum = 0;
            foreach (string token in tokens)
            {
                if (idf.TryGetValue(token, out double w))
                {
                    sum += w;
                }
            }
            return sum;
        }

        double shared = 0;
        HashSet<string> smallerSet = a.Count <= b.Count ? a : b;
        HashSet<string> largerSet = ReferenceEquals(smallerSet, a) ? b : a;
        foreach (string token in smallerSet)
        {
            if (largerSet.Contains(token) && idf.TryGetValue(token, out double w))
            {
                shared += w;
            }
        }

        double denominator = Math.Min(Weight(a), Weight(b));
        return denominator <= 0 ? 0 : shared / denominator;
    }
}
