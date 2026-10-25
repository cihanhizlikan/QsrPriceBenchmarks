using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Playwright;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Services;

namespace QsrPriceBenchmarks.Core.Scraping;

/// <summary>
/// Step 3: fill in missing LATITUDE/LONGITUDE via Google Maps geocoding,
/// then match PLATFORM_LOCATIONS rows to physical LOCATIONS rows.
/// </summary>
public static class GeocodeAndMatchStep
{
    public static async Task RunAsync(
        SqliteConnection conn, long? chainQsrId, bool rematch,
        IProgress<string>? progress = null, CancellationToken ct = default,
        IProgress<StepProgress>? progressCount = null)
    {
        if (rematch)
        {
            using SqliteCommand resetCmd = conn.CreateCommand();
            // One constant statement, the same optional-filter shape as the
            // queries below: a null $qsr resets every chain.
            resetCmd.CommandText =
                "UPDATE PLATFORM_LOCATIONS SET LOCATION_ID = NULL WHERE ($qsr IS NULL OR QSR_ID = $qsr);";
            resetCmd.Parameters.AddWithValue("$qsr", (object?)chainQsrId ?? DBNull.Value);
            resetCmd.ExecuteNonQuery();
            progress?.Report("  ✓ LOCATION_ID reset — rematching from scratch");
        }

        List<(long Id, string Slug, string? Name, string? Address, long QsrId)> nullLocs = LoadMissingCoordLocations(conn, chainQsrId);
        List<(long Id, string Slug, string? Name, string Address)> nullPls = LoadMissingCoordPlatformLocations(conn, chainQsrId);
        int unmatchedCount = CountUnmatched(conn, chainQsrId);

        progress?.Report($"  Gaps: {nullLocs.Count} LOCATIONS + {nullPls.Count} PLATFORM_LOCATIONS " +
            $"without coords, {unmatchedCount} unmatched.");

        if (nullLocs.Count > 0 || nullPls.Count > 0)
        {
            int geoTotal = nullLocs.Count + nullPls.Count;
            int geoDone = 0;

            await using BrowserSession session = await BrowserSession.LaunchAsync();

            // One page per concurrency slot — geocoding drives a browser, so each
            // parallel slot needs its own page. SQLite writes are NOT parallel:
            // a single connection isn't safe for concurrent use, so results are
            // applied sequentially on this thread after each batch completes.
            const int MaxConcurrency = 4;
            List<IPage> pages = [session.Page];
            for (int k = 1; k < MaxConcurrency; k++)
            {
                pages.Add(await session.Context.NewPageAsync());
            }

            int concurrency = 1;   // ramps up to MaxConcurrency while Google keeps up

            // Adaptive (AIMD) geocoding of a work list: run a batch of size
            // `concurrency` in parallel; if none of them strained Google
            // (no timeouts/errors), additively raise the batch size by 1 (capped
            // at MaxConcurrency); if any did, multiplicatively halve it (floored
            // at 1). Returns how many coordinates were resolved.
            async Task<int> RunAdaptiveAsync(List<(string Query, Action<double, double> Apply)> work)
            {
                int success = 0, idx = 0;
                while (idx < work.Count)
                {
                    ct.ThrowIfCancellationRequested();
                    int batchSize = Math.Min(concurrency, work.Count - idx);

                    Task<Geocoder.GeocodeResult>[] tasks = new Task<Geocoder.GeocodeResult>[batchSize];
                    for (int b = 0; b < batchSize; b++)
                    {
                        tasks[b] = Geocoder.GeocodeDetailedAsync(pages[b], work[idx + b].Query);
                    }
                    Geocoder.GeocodeResult[] results = await Task.WhenAll(tasks);

                    int strain = 0;
                    for (int b = 0; b < batchSize; b++)
                    {
                        Geocoder.GeocodeResult r = results[b];
                        if (r.Point is not null)
                        {
                            work[idx + b].Apply(r.Point.Value.Latitude, r.Point.Value.Longitude);
                            success++;
                        }
                        if (r.Outcome is Geocoder.GeocodeOutcome.TimedOut or Geocoder.GeocodeOutcome.Error)
                        {
                            strain++;
                        }
                        progressCount?.Report(new StepProgress("Geocoding", ++geoDone, geoTotal));
                    }
                    idx += batchSize;

                    concurrency = strain == 0
                        ? Math.Min(MaxConcurrency, concurrency + 1)
                        : Math.Max(1, concurrency / 2);
                }
                return success;
            }

            if (nullLocs.Count > 0)
            {
                progress?.Report($"  → Geocoding {nullLocs.Count} LOCATIONS");
                List<(string Query, Action<double, double> Apply)> work = new(nullLocs.Count);
                foreach ((_, string slug, string? name, string? address, long qsrId) in nullLocs)
                {
                    string query = BuildGeocodeQuery(name, address);
                    work.Add((query, (lat, lon) => Repository.UpdateLocationCoords(conn, slug, lat, lon, qsrId)));
                }
                int done = await RunAdaptiveAsync(work);
                progress?.Report($"  ✓ {done}/{nullLocs.Count} LOCATIONS geocoded.");
            }

            if (nullPls.Count > 0)
            {
                progress?.Report($"  → Geocoding {nullPls.Count} PLATFORM_LOCATIONS");
                List<(string Query, Action<double, double> Apply)> work = new(nullPls.Count);
                foreach ((long id, _, string? name, string address) in nullPls)
                {
                    string query = BuildGeocodeQuery(name, address);
                    work.Add((query, (lat, lon) => Repository.UpdatePlatformLocation(conn, id, lat: lat, lon: lon)));
                }
                int done = await RunAdaptiveAsync(work);
                progress?.Report($"  ✓ {done}/{nullPls.Count} PLATFORM_LOCATIONS geocoded.");
            }

            progressCount?.Report(new StepProgress("Geocoding", geoTotal, geoTotal));
        }

        await RunMatchingAsync(conn, chainQsrId, progress);
    }

    private static Task RunMatchingAsync(SqliteConnection conn, long? chainQsrId, IProgress<string>? progress)
    {
        List<(long Id, long QsrId, string Slug, string? Address, double? Latitude, double? Longitude)> unmatchedPls =
            LoadUnmatchedPlatformLocations(conn, chainQsrId);
        if (unmatchedPls.Count == 0)
        {
            progress?.Report("  ✓ Nothing to match.");
            return Task.CompletedTask;
        }

        progress?.Report($"  → Matching {unmatchedPls.Count} unmatched PLATFORM_LOCATIONS");

        // QSR_ID → chain display name, so the matcher can discount the chain's
        // own brand tokens ("burger", "king") that sit in every platform slug.
        Dictionary<long, string> chainNames = Models.ChainRegistry.All.ToDictionary(
            c => Repository.QsrId(conn, c.Name),
            c => c.Name);

        // Group by QSR_ID — matching is always within the same chain.
        IEnumerable<IGrouping<long, (long Id, long QsrId, string Slug, string? Address, double? Latitude, double? Longitude)>> byQsr =
            unmatchedPls.GroupBy(p => p.QsrId);
        int matched = 0, ambiguous = 0, unmatched = 0;

        foreach (IGrouping<long, (long Id, long QsrId, string Slug, string? Address, double? Latitude, double? Longitude)> group in byQsr)
        {
            long qsrId = group.Key;
            List<(long Id, string Slug, string? Address, double? Latitude, double? Longitude)> locations =
                LoadLocationsForMatching(conn, qsrId);

            List<MatchCandidate> candidates = [.. group.Select(p =>
                new MatchCandidate(p.Id, p.Slug, p.Address, p.Latitude, p.Longitude))];
            List<MatchCandidate> locCandidates = [.. locations.Select(l =>
                new MatchCandidate(l.Id, l.Slug, l.Address, l.Latitude, l.Longitude))];

            List<MatchResult> results = Matcher.Match(
                candidates, locCandidates, chainNames.GetValueOrDefault(qsrId));

            foreach (MatchResult r in results)
            {
                if (r.LocationId is not null && !r.Ambiguous)
                {
                    Repository.SetPlatformLocationMatch(conn, r.PlatformLocationId, r.LocationId);
                    progress?.Report($"    → matched [pl: {r.PlatformLocationId}, loc: {r.LocationId}, score: {r.Score:F3}]");
                    matched++;
                }
                else if (r.Ambiguous)
                {
                    ambiguous++;
                }
                else
                {
                    unmatched++;
                }
            }
        }

        if (ambiguous > 0)
        {
            progress?.Report($"  ⚠ {ambiguous} ambiguous (two close scores) — flag for manual review");
        }

        progress?.Report($"  ✓ matched {matched} | ambiguous {ambiguous} | unmatched {unmatched}");
        return Task.CompletedTask;
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    private static List<(long Id, string Slug, string? Name, string? Address, long QsrId)>
        LoadMissingCoordLocations(SqliteConnection conn, long? chainQsrId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ID, SLUG, NAME, ADDRESS, QSR_ID FROM LOCATIONS
            WHERE ADDRESS IS NOT NULL AND ADDRESS != ''
              AND (LATITUDE IS NULL OR LONGITUDE IS NULL)
              AND ($qsr IS NULL OR QSR_ID = $qsr)
            ORDER BY ID;
            """;
        cmd.Parameters.AddWithValue("$qsr", (object?)chainQsrId ?? DBNull.Value);

        List<(long, string, string?, string?, long)> result = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt64(4)));
        }
        return result;
    }

    /// <summary>
    /// Compose the Google Maps query for one location: "name address" when a
    /// name exists (the branch/mall name disambiguates identical street
    /// addresses), anchored to Türkiye when the address doesn't already say so —
    /// identical street names exist across borders and Google happily returns
    /// the wrong country's pin, which then poisons the distance-based matching.
    /// </summary>
    private static string BuildGeocodeQuery(string? name, string? address)
    {
        string query = string.IsNullOrWhiteSpace(name)
            ? (address ?? "")
            : $"{name} {address}".Trim();
        if (!query.Contains("Türkiye", StringComparison.OrdinalIgnoreCase)
            && !query.Contains("Turkiye", StringComparison.OrdinalIgnoreCase)
            && !query.Contains("Turkey", StringComparison.OrdinalIgnoreCase))
        {
            query += ", Türkiye";
        }
        return query;
    }

    private static List<(long Id, string Slug, string? Name, string Address)>
        LoadMissingCoordPlatformLocations(SqliteConnection conn, long? chainQsrId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ID, SLUG, NAME, ADDRESS FROM PLATFORM_LOCATIONS
            WHERE ACTIVE = 1
              AND ADDRESS IS NOT NULL AND ADDRESS != ''
              AND (LATITUDE IS NULL OR LONGITUDE IS NULL)
              AND ($qsr IS NULL OR QSR_ID = $qsr)
            ORDER BY ID;
            """;
        cmd.Parameters.AddWithValue("$qsr", (object?)chainQsrId ?? DBNull.Value);

        List<(long, string, string?, string)> result = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetString(3)));
        }
        return result;
    }

    private static int CountUnmatched(SqliteConnection conn, long? chainQsrId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM PLATFORM_LOCATIONS " +
            "WHERE ACTIVE = 1 AND LOCATION_ID IS NULL AND ($qsr IS NULL OR QSR_ID = $qsr);";
        cmd.Parameters.AddWithValue("$qsr", (object?)chainQsrId ?? DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static List<(long Id, long QsrId, string Slug, string? Address, double? Latitude, double? Longitude)>
        LoadUnmatchedPlatformLocations(SqliteConnection conn, long? chainQsrId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT ID, QSR_ID, SLUG, ADDRESS, LATITUDE, LONGITUDE FROM PLATFORM_LOCATIONS " +
            "WHERE ACTIVE = 1 AND LOCATION_ID IS NULL AND ($qsr IS NULL OR QSR_ID = $qsr);";
        cmd.Parameters.AddWithValue("$qsr", (object?)chainQsrId ?? DBNull.Value);

        List<(long, long, string, string?, double?, double?)> result = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4),
                reader.IsDBNull(5) ? null : reader.GetDouble(5)));
        }
        return result;
    }

    private static List<(long Id, string Slug, string? Address, double? Latitude, double? Longitude)>
        LoadLocationsForMatching(SqliteConnection conn, long qsrId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ID, SLUG, ADDRESS, LATITUDE, LONGITUDE FROM LOCATIONS WHERE QSR_ID = $qsr;";
        cmd.Parameters.AddWithValue("$qsr", qsrId);

        List<(long, string, string?, double?, double?)> result = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((
                reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetDouble(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4)));
        }
        return result;
    }
}
