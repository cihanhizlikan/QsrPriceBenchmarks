using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Core.Util;

namespace QsrPriceBenchmarks.Core.Db;

/// <summary>
/// CRUD operations against the schema in <see cref="Database"/>. Mirrors the
/// Python prototype's upsert_location / update_location_details /
/// upsert_platform_location / update_platform_location functions, including
/// every bugfix found during development:
///   - test-slug filtering (slugs containing "test" are never inserted)
///   - LATITUDE/LONGITUDE nulled whenever the ADDRESS actually changes (in
///     both LOCATIONS and PLATFORM_LOCATIONS) so Step 3 re-geocodes it
///   - slogan-looking addresses (no digits, ends with "!") rejected
///   - empty-string vs. NULL handled correctly (empty never overwrites
///     a previously-good value)
/// </summary>
public static class Repository
{
    /// <summary>
    /// UTC "now" in the sortable, second-resolution format used for every stored
    /// timestamp (SCRAPE_RUNS.STARTED_AT/FINISHED_AT, LOCATIONS.LAST_SEEN_AT, and
    /// the Step 1 crawl window). Centralised so the format can't drift between
    /// call sites — string comparisons on these values depend on a single format.
    /// </summary>
    internal static string UtcStamp() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    // ── Lookups ──────────────────────────────────────────────────────────────

    public static long QsrId(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ID FROM QSR WHERE NAME = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        var result = cmd.ExecuteScalar()
            ?? throw new InvalidOperationException($"QSR not found: {name}");
        return Convert.ToInt64(result);
    }

    public static long PlatformId(SqliteConnection conn, string name)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ID FROM PLATFORMS WHERE NAME = $name;";
        cmd.Parameters.AddWithValue("$name", name);
        var result = cmd.ExecuteScalar()
            ?? throw new InvalidOperationException($"Platform not found: {name}");
        return Convert.ToInt64(result);
    }

    public static List<string> LoadScrapeTabs(SqliteConnection conn, long qsrId, long platformId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TAB_NAME FROM SCRAPE_TABS
            WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat
            ORDER BY DISPLAY_ORDER;
            """;
        cmd.Parameters.AddWithValue("$qsr", qsrId);
        cmd.Parameters.AddWithValue("$plat", platformId);

        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add(reader.GetString(0));
        return result;
    }

    public static string PlatformSlugFor(SqliteConnection conn, long qsrId, long platformId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SLUG FROM QSR_PLATFORMS WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat;
            """;
        cmd.Parameters.AddWithValue("$qsr", qsrId);
        cmd.Parameters.AddWithValue("$plat", platformId);
        var result = cmd.ExecuteScalar()
            ?? throw new InvalidOperationException("QSR_PLATFORMS row not found");
        return (string)result;
    }

    // ── Provinces / Districts ──────────────────────────────────────────────────

    private static long GetOrCreateProvince(SqliteConnection conn, string name)
    {
        using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT ID FROM PROVINCES WHERE NAME = $name;";
            find.Parameters.AddWithValue("$name", name);
            var existing = find.ExecuteScalar();
            if (existing is not null) return Convert.ToInt64(existing);
        }
        using var insert = conn.CreateCommand();
        insert.CommandText = "INSERT INTO PROVINCES (NAME) VALUES ($name); SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(insert.ExecuteScalar());
    }

    private static long GetOrCreateDistrict(SqliteConnection conn, long provinceId, string name)
    {
        using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT ID FROM DISTRICTS WHERE PROVINCE_ID = $province AND NAME = $name;";
            find.Parameters.AddWithValue("$province", provinceId);
            find.Parameters.AddWithValue("$name", name);
            var existing = find.ExecuteScalar();
            if (existing is not null) return Convert.ToInt64(existing);
        }
        using var insert = conn.CreateCommand();
        insert.CommandText =
            "INSERT INTO DISTRICTS (PROVINCE_ID, NAME) VALUES ($province, $name); SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$province", provinceId);
        insert.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(insert.ExecuteScalar());
    }

    // ── LOCATIONS ────────────────────────────────────────────────────────────

    /// <summary>
    /// Ensure PROVINCES / DISTRICTS / LOCATIONS rows exist for this slug.
    /// Returns the LOCATIONS.ID, or null when the slug contains "test" (such
    /// rows are never recorded).
    /// </summary>
    public static long? UpsertLocation(
        SqliteConnection conn, string province, string district, string slug, long qsrId)
    {
        if (TextNormalization.IsTestSlug(slug))
        {
            return null;
        }

        long provinceId = GetOrCreateProvince(conn, province);
        long districtId = GetOrCreateDistrict(conn, provinceId, district);
        string now = UtcStamp();

        using (SqliteCommand find = conn.CreateCommand())
        {
            find.CommandText = "SELECT ID FROM LOCATIONS WHERE QSR_ID = $qsr AND SLUG = $slug;";
            find.Parameters.AddWithValue("$qsr", qsrId);
            find.Parameters.AddWithValue("$slug", slug);
            object? existing = find.ExecuteScalar();
            if (existing is not null)
            {
                long existingId = Convert.ToInt64(existing);
                // Seen this crawl: refresh LAST_SEEN_AT and reactivate, so a slug
                // that had been marked closed in an earlier reconcile comes back
                // on the same row (continuous history, stable LOCATION_ID).
                using (SqliteCommand touch = conn.CreateCommand())
                {
                    touch.CommandText =
                        "UPDATE LOCATIONS SET LAST_SEEN_AT = $now, ACTIVE = 1 WHERE ID = $id;";
                    touch.Parameters.AddWithValue("$now", now);
                    touch.Parameters.AddWithValue("$id", existingId);
                    touch.ExecuteNonQuery();
                }
                return existingId;
            }
        }

        using SqliteCommand insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO LOCATIONS (QSR_ID, DISTRICT_ID, SLUG, ACTIVE, LAST_SEEN_AT)
            VALUES ($qsr, $prov, $slug, 1, $now);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$qsr", qsrId);
        insert.Parameters.AddWithValue("$prov", districtId);
        insert.Parameters.AddWithValue("$slug", slug);
        insert.Parameters.AddWithValue("$now", now);
        return Convert.ToInt64(insert.ExecuteScalar());
    }

    /// <summary>
    /// Soft-close LOCATIONS for one chain that a crawl beginning at
    /// <paramref name="crawlStartUtc"/> (ISO-8601) did not re-discover — but only
    /// within districts that were actually visited this pass. A district counts
    /// as visited when at least one of its locations was re-stamped this pass;
    /// stale rows in districts the crawl never reached (e.g. a district link that
    /// silently dropped off its province page) are left untouched, so a missed page
    /// can't be misread as a wave of closures. Within a visited district, rows
    /// whose LAST_SEEN_AT predates the crawl (or was never stamped) flip to
    /// ACTIVE = 0. The rows, their PLATFORM_LOCATIONS matches, and price history
    /// are all kept, so historical reports stay whole and a reopened slug
    /// reactivates the same row. PROVINCE/DISTRICT reference data is never touched.
    /// Returns the count of locations newly closed by this call.
    /// </summary>
    public static int DeactivateStaleLocations(SqliteConnection conn, long qsrId, string crawlStartUtc)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE LOCATIONS
               SET ACTIVE = 0
             WHERE QSR_ID = $qsr
               AND ACTIVE = 1
               AND (LAST_SEEN_AT IS NULL OR LAST_SEEN_AT < $start)
               AND DISTRICT_ID IN (
                   SELECT DISTINCT DISTRICT_ID
                     FROM LOCATIONS
                    WHERE QSR_ID = $qsr
                      AND DISTRICT_ID IS NOT NULL
                      AND LAST_SEEN_AT >= $start
               );
            """;
        cmd.Parameters.AddWithValue("$qsr", qsrId);
        cmd.Parameters.AddWithValue("$start", crawlStartUtc);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Update NAME/ADDRESS for an existing LOCATIONS row. Nulls
    /// LATITUDE/LONGITUDE whenever the ADDRESS actually changes (so Step 3
    /// re-geocodes); a name-only change leaves coordinates intact. Rejects
    /// slogan-looking addresses (no digits, ends with "!") — these come from
    /// chain-website navigation links that look like restaurant cards but aren't.
    /// An empty/whitespace name or address is treated as "no update for that
    /// field" — never overwrites a previously good value with blank.
    /// </summary>
    public static void UpdateLocationDetails(
        SqliteConnection conn, string slug, string? rawName, string? rawAddress, long qsrId)
    {
        var cleanName = TextNormalization.StripBoilerplate(rawName);
        var cleanAddress = rawAddress?.Trim() ?? "";

        if (!string.IsNullOrEmpty(cleanName) && cleanAddress.Length > 0)
            cleanName = TextNormalization.StripAddressSuffix(cleanName, cleanAddress);

        if (TextNormalization.LooksLikeSlogan(cleanAddress))
            cleanAddress = "";

        var newName = TextNormalization.Title(cleanName);
        var newAddress = TextNormalization.Title(cleanAddress);

        // Title() returns null for empty/whitespace, so "is not null" means
        // "this page produced a usable value for that field". A field whose new
        // value is null is left untouched — never blanked over a good value.
        var writeName = newName is not null;
        var writeAddress = newAddress is not null;
        if (!writeName && !writeAddress)
            return; // nothing usable came from this page — preserve existing row.

        (_, string? existingAddress) = GetLocationNameAddress(conn, slug, qsrId);

        // Coordinates are invalidated only when the ADDRESS we're writing
        // actually changes — the geocoder keys on the address, so a name-only
        // edit must NOT trigger a needless (and flaky) re-geocode.
        bool addressChanged = writeAddress && existingAddress != newAddress;

        var setClauses = new List<string>();
        if (writeName) setClauses.Add("NAME = $name");
        if (writeAddress) setClauses.Add("ADDRESS = $addr");
        if (addressChanged)
        {
            setClauses.Add("LATITUDE = NULL");
            setClauses.Add("LONGITUDE = NULL");
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"UPDATE LOCATIONS SET {string.Join(", ", setClauses)} WHERE SLUG = $slug AND QSR_ID = $qsr;";
        if (writeName) cmd.Parameters.AddWithValue("$name", newName!);
        if (writeAddress) cmd.Parameters.AddWithValue("$addr", newAddress!);
        cmd.Parameters.AddWithValue("$slug", slug);
        cmd.Parameters.AddWithValue("$qsr", qsrId);
        cmd.ExecuteNonQuery();
    }

    private static (string? Name, string? Address) GetLocationNameAddress(
        SqliteConnection conn, string slug, long qsrId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT NAME, ADDRESS FROM LOCATIONS WHERE SLUG = $slug AND QSR_ID = $qsr;";
        cmd.Parameters.AddWithValue("$slug", slug);
        cmd.Parameters.AddWithValue("$qsr", qsrId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return (null, null);
        var name = reader.IsDBNull(0) ? null : reader.GetString(0);
        var addr = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (name, addr);
    }

    public static void UpdateLocationCoords(
        SqliteConnection conn, string slug, double lat, double lon, long qsrId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE LOCATIONS SET LATITUDE = $lat, LONGITUDE = $lon
            WHERE SLUG = $slug AND QSR_ID = $qsr;
            """;
        cmd.Parameters.AddWithValue("$lat", lat);
        cmd.Parameters.AddWithValue("$lon", lon);
        cmd.Parameters.AddWithValue("$slug", slug);
        cmd.Parameters.AddWithValue("$qsr", qsrId);
        cmd.ExecuteNonQuery();
    }

    // ── PLATFORM_LOCATIONS ───────────────────────────────────────────────────

    /// <summary>
    /// Insert a PLATFORM_LOCATIONS row if not present. Returns its ID, or
    /// null when the slug contains "test".
    /// </summary>
    public static long? UpsertPlatformLocation(
        SqliteConnection conn, long qsrId, long platformId, string slug, string? name = null)
    {
        if (TextNormalization.IsTestSlug(slug))
            return null;

        using (var find = conn.CreateCommand())
        {
            find.CommandText = """
                SELECT ID FROM PLATFORM_LOCATIONS
                WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat AND SLUG = $slug;
                """;
            find.Parameters.AddWithValue("$qsr", qsrId);
            find.Parameters.AddWithValue("$plat", platformId);
            find.Parameters.AddWithValue("$slug", slug);
            var existing = find.ExecuteScalar();
            if (existing is not null)
                return Convert.ToInt64(existing);
        }

        using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO PLATFORM_LOCATIONS (QSR_ID, PLATFORM_ID, SLUG, NAME)
            VALUES ($qsr, $plat, $slug, $name);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$qsr", qsrId);
        insert.Parameters.AddWithValue("$plat", platformId);
        insert.Parameters.AddWithValue("$slug", slug);
        insert.Parameters.AddWithValue("$name", (object?)TextNormalization.Title(name) ?? DBNull.Value);
        return Convert.ToInt64(insert.ExecuteScalar());
    }

    /// <summary>
    /// Insert many PLATFORM_LOCATIONS slugs (NAME left null) in a single
    /// transaction with prepared, reused commands. Same per-slug semantics as
    /// <see cref="UpsertPlatformLocation"/>: "test" slugs are skipped and
    /// already-present slugs are left untouched. Returns the number actually
    /// inserted. Used by the sitemap harvest, where a slug-by-slug autocommit
    /// loop would otherwise fsync once per row across thousands of slugs.
    /// </summary>
    public static int UpsertPlatformLocationsBulk(
        SqliteConnection conn, long qsrId, long platformId, IEnumerable<string> slugs)
    {
        using var tx = conn.BeginTransaction();

        using var find = conn.CreateCommand();
        find.Transaction = tx;
        find.CommandText = """
            SELECT 1 FROM PLATFORM_LOCATIONS
            WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat AND SLUG = $slug;
            """;
        find.Parameters.AddWithValue("$qsr", qsrId);
        find.Parameters.AddWithValue("$plat", platformId);
        var findSlug = find.Parameters.Add("$slug", SqliteType.Text);

        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO PLATFORM_LOCATIONS (QSR_ID, PLATFORM_ID, SLUG)
            VALUES ($qsr, $plat, $slug);
            """;
        insert.Parameters.AddWithValue("$qsr", qsrId);
        insert.Parameters.AddWithValue("$plat", platformId);
        var insertSlug = insert.Parameters.Add("$slug", SqliteType.Text);

        int inserted = 0;
        foreach (var slug in slugs)
        {
            if (TextNormalization.IsTestSlug(slug))
                continue;

            findSlug.Value = slug;
            if (find.ExecuteScalar() is not null)
                continue; // already present

            insertSlug.Value = slug;
            insert.ExecuteNonQuery();
            inserted++;
        }

        tx.Commit();
        return inserted;
    }

    /// <summary>
    /// Update NAME/ADDRESS/LATITUDE/LONGITUDE on a PLATFORM_LOCATIONS row.
    /// Nulls LATITUDE/LONGITUDE when ADDRESS actually changes — geocoding
    /// must re-run. Only non-null parameters are updated.
    /// </summary>
    public static void UpdatePlatformLocation(
        SqliteConnection conn, long platformLocationId,
        string? name = null, string? address = null,
        double? lat = null, double? lon = null)
    {
        var setClauses = new List<string>();
        var parameters = new List<(string Name, object Value)>();

        if (name is not null)
        {
            setClauses.Add("NAME = $name");
            parameters.Add(("$name", (object?)TextNormalization.Title(name) ?? DBNull.Value));
        }

        if (address is not null)
        {
            var newAddr = TextNormalization.Title(address);

            string? existingAddr;
            using (var find = conn.CreateCommand())
            {
                find.CommandText = "SELECT ADDRESS FROM PLATFORM_LOCATIONS WHERE ID = $id;";
                find.Parameters.AddWithValue("$id", platformLocationId);
                existingAddr = find.ExecuteScalar() as string;
            }

            setClauses.Add("ADDRESS = $addr");
            parameters.Add(("$addr", (object?)newAddr ?? DBNull.Value));

            // ADDRESS changed → coordinates are stale; force a re-geocode.
            if (existingAddr != newAddr)
            {
                setClauses.Add("LATITUDE = NULL");
                setClauses.Add("LONGITUDE = NULL");
            }
        }

        if (lat is not null)
        {
            setClauses.Add("LATITUDE = $lat");
            parameters.Add(("$lat", lat.Value));
        }
        if (lon is not null)
        {
            setClauses.Add("LONGITUDE = $lon");
            parameters.Add(("$lon", lon.Value));
        }

        if (setClauses.Count == 0)
            return;

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"UPDATE PLATFORM_LOCATIONS SET {string.Join(", ", setClauses)} WHERE ID = $id;";
        foreach (var (pName, pValue) in parameters)
            cmd.Parameters.AddWithValue(pName, pValue);
        cmd.Parameters.AddWithValue("$id", platformLocationId);
        cmd.ExecuteNonQuery();
    }

    public static void SetPlatformLocationMatch(SqliteConnection conn, long platformLocationId, long? locationId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE PLATFORM_LOCATIONS SET LOCATION_ID = $loc WHERE ID = $id;";
        cmd.Parameters.AddWithValue("$loc", (object?)locationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", platformLocationId);
        cmd.ExecuteNonQuery();
    }

    // ── MENU_ITEMS ───────────────────────────────────────────────────────────

    public static long GetOrCreateMenuItem(SqliteConnection conn, long qsrId, string name)
    {
        using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT ID FROM MENU_ITEMS WHERE QSR_ID = $qsr AND NAME = $name;";
            find.Parameters.AddWithValue("$qsr", qsrId);
            find.Parameters.AddWithValue("$name", name);
            var existing = find.ExecuteScalar();
            if (existing is not null) return Convert.ToInt64(existing);
        }
        using var insert = conn.CreateCommand();
        insert.CommandText =
            "INSERT INTO MENU_ITEMS (QSR_ID, NAME) VALUES ($qsr, $name); SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$qsr", qsrId);
        insert.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(insert.ExecuteScalar());
    }

    // ── SCRAPE_RUNS / MENU_SNAPSHOTS ─────────────────────────────────────────

    public static long StartScrapeRun(SqliteConnection conn, long qsrId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO SCRAPE_RUNS (QSR_ID, STARTED_AT) VALUES ($qsr, $started);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$qsr", qsrId);
        cmd.Parameters.AddWithValue("$started", UtcStamp());
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public static void FinishScrapeRun(SqliteConnection conn, long runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE SCRAPE_RUNS SET FINISHED_AT = $finished WHERE ID = $id;";
        cmd.Parameters.AddWithValue("$finished", UtcStamp());
        cmd.Parameters.AddWithValue("$id", runId);
        cmd.ExecuteNonQuery();
    }

    public static void InsertSnapshot(
        SqliteConnection conn, long runId, long platformLocationId, long menuItemId, decimal price)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO MENU_SNAPSHOTS (SCRAPE_RUN_ID, PLATFORM_LOCATION_ID, MENU_ITEM_ID, PRICE)
            VALUES ($run, $pl, $item, $price);
            """;
        cmd.Parameters.AddWithValue("$run", runId);
        cmd.Parameters.AddWithValue("$pl", platformLocationId);
        cmd.Parameters.AddWithValue("$item", menuItemId);
        cmd.Parameters.AddWithValue("$price", price);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Platform locations that produced ZERO menu snapshots in their chain's
    /// most recent COMPLETED scrape run (FINISHED_AT not null). Using the latest
    /// completed run per chain reflects the current state — a location that
    /// failed in an older run but succeeded later is not listed. Chains without
    /// any completed run are excluded entirely.
    ///
    /// Caveat: the schema records no per-run "attempted" set, so a location
    /// added AFTER its chain's last completed run (hence never scraped) would
    /// also show 0 here. In the normal harvest→scrape flow that doesn't happen.
    /// </summary>
    public static List<UnscrapedLocationRow> GetUnscrapedPlatformLocations(SqliteConnection conn, long qsrId)
    {
        // chain display name -> Tıkla Gelsin path slug, for building the URL
        // (the chain slug lives in ChainConfig, not the DB).
        Dictionary<string, string> slugByChain = Chains.All.ToDictionary(
            c => c.Name,
            c => c.PlatformSlugs.TryGetValue(c.PrimaryPlatform, out string? s) ? s ?? "" : "",
            StringComparer.OrdinalIgnoreCase);

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH latest_run AS (
                SELECT QSR_ID, MAX(ID) AS RUN_ID
                FROM SCRAPE_RUNS
                WHERE FINISHED_AT IS NOT NULL
                GROUP BY QSR_ID
            )
            SELECT q.NAME, pl.NAME, pl.ADDRESS, pl.SLUG
            FROM PLATFORM_LOCATIONS pl
            JOIN latest_run lr ON lr.QSR_ID = pl.QSR_ID
            JOIN QSR q         ON q.ID      = pl.QSR_ID
            WHERE pl.QSR_ID = $qsr
              AND NOT EXISTS (
                SELECT 1 FROM MENU_SNAPSHOTS ms
                WHERE ms.PLATFORM_LOCATION_ID = pl.ID
                  AND ms.SCRAPE_RUN_ID        = lr.RUN_ID
            )
            ORDER BY pl.SLUG;
            """;
        cmd.Parameters.AddWithValue("$qsr", qsrId);

        List<UnscrapedLocationRow> result = new();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string  qsrName = reader.GetString(0);
            string? name    = reader.IsDBNull(1) ? null : reader.GetString(1);
            string? address = reader.IsDBNull(2) ? null : reader.GetString(2);
            string  slug    = reader.GetString(3);

            string chainSlug = slugByChain.GetValueOrDefault(qsrName, "");
            string url = string.IsNullOrEmpty(chainSlug)
                ? ""
                : $"https://www.tiklagelsin.com/restoran/{chainSlug}/{slug}?gel-al";

            result.Add(new UnscrapedLocationRow(qsrName, name, address, url));
        }
        return result;
    }

    /// <summary>
    /// Location tallies for one chain grouped by province (İl) and district (İlçe),
    /// for the map. Locations with no assigned district are excluded (they have
    /// no province to plot against).
    /// </summary>
    public static List<ProvinceDistrictCount> GetLocationCountsByProvince(SqliteConnection conn, long qsrId)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.NAME, p.NAME, COUNT(*)
            FROM LOCATIONS l
            JOIN DISTRICTS p ON p.ID = l.DISTRICT_ID
            JOIN PROVINCES    c ON c.ID = p.PROVINCE_ID
            WHERE l.QSR_ID = $qsr
              AND l.ACTIVE = 1
            GROUP BY c.NAME, p.NAME
            ORDER BY c.NAME, p.NAME;
            """;
        cmd.Parameters.AddWithValue("$qsr", qsrId);

        List<ProvinceDistrictCount> result = new();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ProvinceDistrictCount(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }
        return result;
    }

    /// <summary>
    /// LOCATIONS recorded for one chain (every district, including rows not yet
    /// assigned to a district). With <paramref name="activeOnly"/> the count is
    /// limited to still-listed (ACTIVE = 1) locations — used for the Locations-tab
    /// header. The default counts every row, which keeps the Step 1 "already
    /// crawled" skip check based on whether any crawl data exists at all.
    /// </summary>
    public static int CountLocations(SqliteConnection conn, long qsrId, bool activeOnly = false)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = activeOnly
            ? "SELECT COUNT(*) FROM LOCATIONS WHERE QSR_ID = $qsr AND ACTIVE = 1;"
            : "SELECT COUNT(*) FROM LOCATIONS WHERE QSR_ID = $qsr;";
        cmd.Parameters.AddWithValue("$qsr", qsrId);
        object? scalar = cmd.ExecuteScalar();
        return scalar is null ? 0 : Convert.ToInt32(scalar);
    }

    public static List<ScrapeRunRow> ListScrapeRuns(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ID, QSR_ID, STARTED_AT, FINISHED_AT FROM SCRAPE_RUNS ORDER BY ID;
            """;
        var result = new List<ScrapeRunRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ScrapeRunRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return result;
    }

    public static (bool Found, long QsrId, string StartedAt, int SnapshotCount) GetScrapeRunForDeletion(
        SqliteConnection conn, long runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT QSR_ID, STARTED_AT FROM SCRAPE_RUNS WHERE ID = $id;";
        cmd.Parameters.AddWithValue("$id", runId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return (false, 0, "", 0);

        var qsrId = reader.GetInt64(0);
        var startedAt = reader.GetString(1);
        reader.Close();

        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM MENU_SNAPSHOTS WHERE SCRAPE_RUN_ID = $id;";
        countCmd.Parameters.AddWithValue("$id", runId);
        var count = Convert.ToInt32(countCmd.ExecuteScalar());

        return (true, qsrId, startedAt, count);
    }

    public static void DeleteScrapeRun(SqliteConnection conn, long runId)
    {
        using var tx = conn.BeginTransaction();
        using (var del1 = conn.CreateCommand())
        {
            del1.Transaction = tx;
            del1.CommandText = "DELETE FROM MENU_SNAPSHOTS WHERE SCRAPE_RUN_ID = $id;";
            del1.Parameters.AddWithValue("$id", runId);
            del1.ExecuteNonQuery();
        }
        using (var del2 = conn.CreateCommand())
        {
            del2.Transaction = tx;
            del2.CommandText = "DELETE FROM SCRAPE_RUNS WHERE ID = $id;";
            del2.Parameters.AddWithValue("$id", runId);
            del2.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public static int DeleteScrapeRunsBefore(SqliteConnection conn, long runId)
    {
        using var tx = conn.BeginTransaction();

        int affected;
        using (var del1 = conn.CreateCommand())
        {
            del1.Transaction = tx;
            del1.CommandText = """
                DELETE FROM MENU_SNAPSHOTS
                WHERE SCRAPE_RUN_ID IN (SELECT ID FROM SCRAPE_RUNS WHERE ID < $id);
                """;
            del1.Parameters.AddWithValue("$id", runId);
            del1.ExecuteNonQuery();
        }
        using (var del2 = conn.CreateCommand())
        {
            del2.Transaction = tx;
            del2.CommandText = "DELETE FROM SCRAPE_RUNS WHERE ID < $id;";
            del2.Parameters.AddWithValue("$id", runId);
            affected = del2.ExecuteNonQuery();
        }
        tx.Commit();
        return affected;
    }

    // ── Logo blobs ───────────────────────────────────────────────────────────

    /// <summary>Return the stored PNG/SVG logo bytes for a QSR row, or null if none set.</summary>
    public static byte[]? GetLogoBlob(SqliteConnection conn, long qsrId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT LOGO_BLOB FROM QSR WHERE ID = $id;";
        cmd.Parameters.AddWithValue("$id", qsrId);
        var result = cmd.ExecuteScalar();
        return result is DBNull || result is null ? null : (byte[])result;
    }

    public static void SetLogoBlob(SqliteConnection conn, long qsrId, byte[] logoBytes)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE QSR SET LOGO_BLOB = $blob WHERE ID = $id;";
        cmd.Parameters.AddWithValue("$blob", logoBytes);
        cmd.Parameters.AddWithValue("$id", qsrId);
        cmd.ExecuteNonQuery();
    }
}
