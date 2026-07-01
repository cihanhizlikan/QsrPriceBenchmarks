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
            using var resetCmd = conn.CreateCommand();
            resetCmd.CommandText = chainQsrId is not null
                ? "UPDATE PLATFORM_LOCATIONS SET LOCATION_ID = NULL WHERE QSR_ID = $qsr;"
                : "UPDATE PLATFORM_LOCATIONS SET LOCATION_ID = NULL;";
            if (chainQsrId is not null)
                resetCmd.Parameters.AddWithValue("$qsr", chainQsrId.Value);
            resetCmd.ExecuteNonQuery();
            progress?.Report("  ✓ LOCATION_ID reset — rematching from scratch");
        }

        var nullLocs = LoadMissingCoordLocations(conn, chainQsrId);
        var nullPls = LoadMissingCoordPlatformLocations(conn, chainQsrId);
        var unmatchedCount = CountUnmatched(conn, chainQsrId);

        progress?.Report($"  Gaps: {nullLocs.Count} LOCATIONS + {nullPls.Count} PLATFORM_LOCATIONS " +
            $"without coords, {unmatchedCount} unmatched.");

        if (nullLocs.Count > 0 || nullPls.Count > 0)
        {
            var geoTotal = nullLocs.Count + nullPls.Count;
            var geoDone = 0;

            await using var session = await BrowserSession.LaunchAsync();

            // One page per concurrency slot — geocoding drives a browser, so each
            // parallel slot needs its own page. SQLite writes are NOT parallel:
            // a single connection isn't safe for concurrent use, so results are
            // applied sequentially on this thread after each batch completes.
            const int MaxConcurrency = 4;
            var pages = new List<IPage> { session.Page };
            for (int k = 1; k < MaxConcurrency; k++)
                pages.Add(await session.Context.NewPageAsync());

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

                    var tasks = new Task<Geocoder.GeocodeResult>[batchSize];
                    for (int b = 0; b < batchSize; b++)
                        tasks[b] = Geocoder.GeocodeDetailedAsync(pages[b], work[idx + b].Query);
                    var results = await Task.WhenAll(tasks);

                    int strain = 0;
                    for (int b = 0; b < batchSize; b++)
                    {
                        var r = results[b];
                        if (r.Point is not null)
                        {
                            work[idx + b].Apply(r.Point.Value.Latitude, r.Point.Value.Longitude);
                            success++;
                        }
                        if (r.Outcome is Geocoder.GeocodeOutcome.TimedOut or Geocoder.GeocodeOutcome.Error)
                            strain++;
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
                var work = new List<(string Query, Action<double, double> Apply)>(nullLocs.Count);
                foreach (var (_, slug, name, address, qsrId) in nullLocs)
                {
                    var query = string.IsNullOrWhiteSpace(name) ? (address ?? "") : $"{name} {address}";
                    work.Add((query, (lat, lon) => Repository.UpdateLocationCoords(conn, slug, lat, lon, qsrId)));
                }
                var done = await RunAdaptiveAsync(work);
                progress?.Report($"  ✓ {done}/{nullLocs.Count} LOCATIONS geocoded.");
            }

            if (nullPls.Count > 0)
            {
                progress?.Report($"  → Geocoding {nullPls.Count} PLATFORM_LOCATIONS");
                var work = new List<(string Query, Action<double, double> Apply)>(nullPls.Count);
                foreach (var (id, _, address) in nullPls)
                    work.Add((address, (lat, lon) => Repository.UpdatePlatformLocation(conn, id, lat: lat, lon: lon)));
                var done = await RunAdaptiveAsync(work);
                progress?.Report($"  ✓ {done}/{nullPls.Count} PLATFORM_LOCATIONS geocoded.");
            }

            progressCount?.Report(new StepProgress("Geocoding", geoTotal, geoTotal));
        }

        await RunMatchingAsync(conn, chainQsrId, progress);
    }

    private static Task RunMatchingAsync(SqliteConnection conn, long? chainQsrId, IProgress<string>? progress)
    {
        var unmatchedPls = LoadUnmatchedPlatformLocations(conn, chainQsrId);
        if (unmatchedPls.Count == 0)
        {
            progress?.Report("  ✓ Nothing to match.");
            return Task.CompletedTask;
        }

        progress?.Report($"  → Matching {unmatchedPls.Count} unmatched PLATFORM_LOCATIONS");

        // Group by QSR_ID — matching is always within the same chain.
        var byQsr = unmatchedPls.GroupBy(p => p.QsrId);
        int matched = 0, ambiguous = 0, unmatched = 0;

        foreach (var group in byQsr)
        {
            var qsrId = group.Key;
            var locations = LoadLocationsForMatching(conn, qsrId);

            var candidates = group.Select(p =>
                new MatchCandidate(p.Id, p.Slug, p.Address, p.Latitude, p.Longitude)).ToList();
            var locCandidates = locations.Select(l =>
                new MatchCandidate(l.Id, l.Slug, l.Address, l.Latitude, l.Longitude)).ToList();

            var results = Matcher.Match(candidates, locCandidates);

            foreach (var r in results)
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
            progress?.Report($"  ⚠ {ambiguous} ambiguous (two close scores) — flag for manual review");

        progress?.Report($"  ✓ matched {matched} | ambiguous {ambiguous} | unmatched {unmatched}");
        return Task.CompletedTask;
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    private static List<(long Id, string Slug, string? Name, string? Address, long QsrId)>
        LoadMissingCoordLocations(SqliteConnection conn, long? chainQsrId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ID, SLUG, NAME, ADDRESS, QSR_ID FROM LOCATIONS
            WHERE ADDRESS IS NOT NULL AND ADDRESS != ''
              AND (LATITUDE IS NULL OR LONGITUDE IS NULL)
              AND ($qsr IS NULL OR QSR_ID = $qsr)
            ORDER BY ID;
            """;
        cmd.Parameters.AddWithValue("$qsr", (object?)chainQsrId ?? DBNull.Value);

        var result = new List<(long, string, string?, string?, long)>();
        using var reader = cmd.ExecuteReader();
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

    private static List<(long Id, string Slug, string Address)>
        LoadMissingCoordPlatformLocations(SqliteConnection conn, long? chainQsrId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ID, SLUG, ADDRESS FROM PLATFORM_LOCATIONS
            WHERE ADDRESS IS NOT NULL AND ADDRESS != ''
              AND (LATITUDE IS NULL OR LONGITUDE IS NULL)
              AND ($qsr IS NULL OR QSR_ID = $qsr)
            ORDER BY ID;
            """;
        cmd.Parameters.AddWithValue("$qsr", (object?)chainQsrId ?? DBNull.Value);

        var result = new List<(long, string, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return result;
    }

    private static int CountUnmatched(SqliteConnection conn, long? chainQsrId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM PLATFORM_LOCATIONS " +
            "WHERE LOCATION_ID IS NULL AND ($qsr IS NULL OR QSR_ID = $qsr);";
        cmd.Parameters.AddWithValue("$qsr", (object?)chainQsrId ?? DBNull.Value);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static List<(long Id, long QsrId, string Slug, string? Address, double? Latitude, double? Longitude)>
        LoadUnmatchedPlatformLocations(SqliteConnection conn, long? chainQsrId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT ID, QSR_ID, SLUG, ADDRESS, LATITUDE, LONGITUDE FROM PLATFORM_LOCATIONS " +
            "WHERE LOCATION_ID IS NULL AND ($qsr IS NULL OR QSR_ID = $qsr);";
        cmd.Parameters.AddWithValue("$qsr", (object?)chainQsrId ?? DBNull.Value);

        var result = new List<(long, long, string, string?, double?, double?)>();
        using var reader = cmd.ExecuteReader();
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
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ID, SLUG, ADDRESS, LATITUDE, LONGITUDE FROM LOCATIONS WHERE QSR_ID = $qsr;";
        cmd.Parameters.AddWithValue("$qsr", qsrId);

        var result = new List<(long, string, string?, double?, double?)>();
        using var reader = cmd.ExecuteReader();
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
