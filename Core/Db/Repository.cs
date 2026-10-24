using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
    internal static string UtcStamp()
    {
        return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }

    // ── Command plumbing ─────────────────────────────────────────────────────
    // Every ADO.NET call in this class runs through these helpers, so the
    // create-command / set-text / add-parameters boilerplate lives in exactly
    // one place. Null parameter values are stored as SQL NULL (DBNull).

    [SuppressMessage("Security", "CA2100", Justification = "Every caller builds its SQL from constant fragments (the dynamic UPDATEs join constant SET clauses) and binds values as parameters.")]
    private static SqliteCommand Command(
        SqliteConnection conn, string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object? value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return cmd;
    }

    private static SqliteCommand Command(
        SqliteConnection conn, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        SqliteCommand cmd = Command(conn, sql, parameters);
        cmd.Transaction = tx;
        return cmd;
    }

    /// <summary>ExecuteScalar with parameters; null when the query matches nothing.</summary>
    private static object? Scalar(
        SqliteConnection conn, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand cmd = Command(conn, sql, parameters);
        return cmd.ExecuteScalar();
    }

    /// <summary>ExecuteNonQuery with parameters; returns the affected-row count.</summary>
    private static int NonQuery(
        SqliteConnection conn, string sql, params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand cmd = Command(conn, sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>ExecuteNonQuery inside an open transaction (required by Microsoft.Data.Sqlite).</summary>
    private static int NonQuery(
        SqliteConnection conn, SqliteTransaction tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using SqliteCommand cmd = Command(conn, tx, sql, parameters);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Find-or-insert for simple lookup tables. Runs <paramref name="findSql"/>
    /// first and returns the existing ID when it matches; otherwise runs
    /// <paramref name="insertSql"/> (which must end in SELECT last_insert_rowid())
    /// and returns the new ID. Both statements receive the same parameter set.
    /// </summary>
    private static long GetOrCreateId(
        SqliteConnection conn, string findSql, string insertSql,
        params (string Name, object? Value)[] parameters)
    {
        object? existing = Scalar(conn, findSql, parameters);
        if (existing is not null)
        {
            return Convert.ToInt64(existing, CultureInfo.InvariantCulture);
        }
        return Convert.ToInt64(Scalar(conn, insertSql, parameters), CultureInfo.InvariantCulture);
    }

    // ── Lookups ──────────────────────────────────────────────────────────────

    public static long QsrId(SqliteConnection conn, string name)
    {
        object? result = Scalar(conn, "SELECT ID FROM QSR WHERE NAME = $name;", ("$name", name))
            ?? throw new InvalidOperationException($"QSR not found: {name}");
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    public static long PlatformId(SqliteConnection conn, string name)
    {
        object? result = Scalar(conn, "SELECT ID FROM PLATFORMS WHERE NAME = $name;", ("$name", name))
            ?? throw new InvalidOperationException($"Platform not found: {name}");
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <summary>Active (not soft-deleted) scrape tabs for one chain/platform, in display order.</summary>
    public static List<string> LoadScrapeTabs(SqliteConnection conn, long qsrId, long platformId)
    {
        using SqliteCommand cmd = Command(conn, """
            SELECT TAB_NAME FROM SCRAPE_TABS
            WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat AND ACTIVE = 1
            ORDER BY DISPLAY_ORDER;
            """,
            ("$qsr", qsrId), ("$plat", platformId));
        return ReadStrings(cmd);
    }

    /// <summary>First column of every row, as strings — shared reader loop.</summary>
    private static List<string> ReadStrings(SqliteCommand cmd)
    {
        List<string> result = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }
        return result;
    }

    /// <summary>
    /// Record a tab seen live on the platform's pages: inserts it (after every
    /// existing tab in display order) or, if a soft-deleted row of the same name
    /// exists, reactivates it — either way stamping LAST_SEEN_AT. Newly recorded
    /// tabs become selectable for FUTURE runs; the caller must not add them to
    /// the run already in progress.
    /// </summary>
    public static void UpsertDiscoveredScrapeTab(
        SqliteConnection conn, long qsrId, long platformId, string tabName)
    {
        NonQuery(conn, """
            INSERT INTO SCRAPE_TABS
                (QSR_ID, PLATFORM_ID, TAB_NAME, DISPLAY_ORDER, ACTIVE, LAST_SEEN_AT)
            VALUES ($qsr, $plat, $tab,
                (SELECT COALESCE(MAX(DISPLAY_ORDER), -1) + 1 FROM SCRAPE_TABS
                 WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat),
                1, $seen)
            ON CONFLICT (QSR_ID, PLATFORM_ID, TAB_NAME)
            DO UPDATE SET ACTIVE = 1, LAST_SEEN_AT = excluded.LAST_SEEN_AT;
            """,
            ("$qsr", qsrId), ("$plat", platformId), ("$tab", tabName), ("$seen", UtcStamp()));
    }

    /// <summary>
    /// Reconcile SCRAPE_TABS against the tabs actually observed during a run
    /// that completed cleanly: observed tabs get LAST_SEEN_AT stamped; active
    /// tabs the run never saw on any platform location are soft-deleted
    /// (ACTIVE = 0, LAST_SEEN_AT untouched — it keeps recording the last run
    /// that DID see the tab). Returns the names deactivated, for logging.
    /// </summary>
    public static List<string> ReconcileScrapeTabs(
        SqliteConnection conn, long qsrId, long platformId, IReadOnlyCollection<string> observedTabs)
    {
        HashSet<string> observed = new(observedTabs, StringComparer.Ordinal);
        List<string> deactivated = [];

        using SqliteTransaction tx = conn.BeginTransaction();
        foreach (string tab in LoadScrapeTabsTx(conn, tx, qsrId, platformId))
        {
            if (observed.Contains(tab))
            {
                NonQuery(conn, tx, """
                    UPDATE SCRAPE_TABS SET LAST_SEEN_AT = $seen
                    WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat AND TAB_NAME = $tab;
                    """,
                    ("$seen", UtcStamp()), ("$qsr", qsrId), ("$plat", platformId), ("$tab", tab));
            }
            else
            {
                NonQuery(conn, tx, """
                    UPDATE SCRAPE_TABS SET ACTIVE = 0
                    WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat AND TAB_NAME = $tab;
                    """,
                    ("$qsr", qsrId), ("$plat", platformId), ("$tab", tab));
                deactivated.Add(tab);
            }
        }
        tx.Commit();
        return deactivated;
    }

    private static List<string> LoadScrapeTabsTx(
        SqliteConnection conn, SqliteTransaction tx, long qsrId, long platformId)
    {
        using SqliteCommand cmd = Command(conn, tx, """
            SELECT TAB_NAME FROM SCRAPE_TABS
            WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat AND ACTIVE = 1
            ORDER BY DISPLAY_ORDER;
            """,
            ("$qsr", qsrId), ("$plat", platformId));
        return ReadStrings(cmd);
    }

    // ── Provinces / Districts ────────────────────────────────────────────────

    private static long GetOrCreateProvince(SqliteConnection conn, string name)
    {
        return GetOrCreateId(conn,
            "SELECT ID FROM PROVINCES WHERE NAME = $name;",
            "INSERT INTO PROVINCES (NAME) VALUES ($name); SELECT last_insert_rowid();",
            ("$name", name));
    }

    private static long GetOrCreateDistrict(SqliteConnection conn, long provinceId, string name)
    {
        return GetOrCreateId(conn,
            "SELECT ID FROM DISTRICTS WHERE PROVINCE_ID = $province AND NAME = $name;",
            "INSERT INTO DISTRICTS (PROVINCE_ID, NAME) VALUES ($province, $name); SELECT last_insert_rowid();",
            ("$province", provinceId), ("$name", name));
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

        object? existing = Scalar(conn,
            "SELECT ID FROM LOCATIONS WHERE QSR_ID = $qsr AND SLUG = $slug;",
            ("$qsr", qsrId), ("$slug", slug));
        if (existing is not null)
        {
            long existingId = Convert.ToInt64(existing, CultureInfo.InvariantCulture);
            // Seen this crawl: refresh LAST_SEEN_AT and reactivate, so a slug
            // that had been marked closed in an earlier reconcile comes back
            // on the same row (continuous history, stable LOCATION_ID).
            NonQuery(conn,
                "UPDATE LOCATIONS SET LAST_SEEN_AT = $now, ACTIVE = 1 WHERE ID = $id;",
                ("$now", now), ("$id", existingId));
            return existingId;
        }

        return Convert.ToInt64(Scalar(conn, """
            INSERT INTO LOCATIONS (QSR_ID, DISTRICT_ID, SLUG, ACTIVE, LAST_SEEN_AT)
            VALUES ($qsr, $district, $slug, 1, $now);
            SELECT last_insert_rowid();
            """,
            ("$qsr", qsrId), ("$district", districtId), ("$slug", slug), ("$now", now)), CultureInfo.InvariantCulture);
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
        return NonQuery(conn, """
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
            """,
            ("$qsr", qsrId), ("$start", crawlStartUtc));
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
        string? cleanName = TextNormalization.StripBoilerplate(rawName);
        string cleanAddress = rawAddress?.Trim() ?? "";

        if (!string.IsNullOrEmpty(cleanName) && cleanAddress.Length > 0)
        {
            cleanName = TextNormalization.StripAddressSuffix(cleanName, cleanAddress);
        }

        if (TextNormalization.LooksLikeSlogan(cleanAddress))
        {
            cleanAddress = "";
        }

        string? newName = TextNormalization.Title(cleanName);
        string? newAddress = TextNormalization.Title(cleanAddress);

        // Title() returns null for empty/whitespace, so "is not null" means
        // "this page produced a usable value for that field". A field whose new
        // value is null is left untouched — never blanked over a good value.
        bool writeName = newName is not null;
        bool writeAddress = newAddress is not null;
        if (!writeName && !writeAddress)
        {
            return; // nothing usable came from this page — preserve existing row.
        }

        (_, string? existingAddress) = GetLocationNameAddress(conn, slug, qsrId);

        // Coordinates are invalidated only when the ADDRESS we're writing
        // actually changes — the geocoder keys on the address, so a name-only
        // edit must NOT trigger a needless (and flaky) re-geocode.
        bool addressChanged = writeAddress && existingAddress != newAddress;

        List<string> setClauses = [];
        if (writeName)
        {
            setClauses.Add("NAME = $name");
        }
        if (writeAddress)
        {
            setClauses.Add("ADDRESS = $addr");
        }
        if (addressChanged)
        {
            setClauses.Add("LATITUDE = NULL");
            setClauses.Add("LONGITUDE = NULL");
        }

        using SqliteCommand cmd = Command(conn,
            $"UPDATE LOCATIONS SET {string.Join(", ", setClauses)} WHERE SLUG = $slug AND QSR_ID = $qsr;",
            ("$slug", slug), ("$qsr", qsrId));
        if (writeName)
        {
            cmd.Parameters.AddWithValue("$name", newName!);
        }
        if (writeAddress)
        {
            cmd.Parameters.AddWithValue("$addr", newAddress!);
        }
        cmd.ExecuteNonQuery();
    }

    private static (string? Name, string? Address) GetLocationNameAddress(
        SqliteConnection conn, string slug, long qsrId)
    {
        using SqliteCommand cmd = Command(conn,
            "SELECT NAME, ADDRESS FROM LOCATIONS WHERE SLUG = $slug AND QSR_ID = $qsr;",
            ("$slug", slug), ("$qsr", qsrId));
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return (null, null);
        }
        string? name = reader.IsDBNull(0) ? null : reader.GetString(0);
        string? address = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (name, address);
    }

    public static void UpdateLocationCoords(
        SqliteConnection conn, string slug, double lat, double lon, long qsrId)
    {
        NonQuery(conn, """
            UPDATE LOCATIONS SET LATITUDE = $lat, LONGITUDE = $lon
            WHERE SLUG = $slug AND QSR_ID = $qsr;
            """,
            ("$lat", lat), ("$lon", lon), ("$slug", slug), ("$qsr", qsrId));
    }

    // ── PLATFORM_LOCATIONS ───────────────────────────────────────────────────

    /// <summary>Outcome of one sitemap census applied to PLATFORM_LOCATIONS.</summary>
    /// <param name="Inserted">Slugs seen for the first time (new rows).</param>
    /// <param name="Reactivated">Previously soft-deleted slugs the census contains again.</param>
    /// <param name="Delisted">Active slugs the census no longer contains (now soft-deleted).</param>
    public sealed record PlatformCensusResult(int Inserted, int Reactivated, List<string> Delisted);

    /// <summary>
    /// Apply one sitemap census to PLATFORM_LOCATIONS in a single transaction:
    /// every listed slug becomes/stays ACTIVE with LAST_SEEN_AT stamped (new
    /// slugs inserted, returning slugs reactivated in place, keeping their
    /// scrape history), and every previously active slug MISSING from the
    /// census is soft-deleted (ACTIVE = 0; LAST_SEEN_AT keeps the last census
    /// that contained it). The sitemap is the platform's own full listing, so
    /// absence from it is authoritative — but an EMPTY census is treated as a
    /// fetch anomaly and changes nothing. "Test" slugs are skipped as before.
    /// </summary>
    public static PlatformCensusResult UpsertPlatformLocationsBulk(
        SqliteConnection conn, long qsrId, long platformId, IEnumerable<string> slugs)
    {
        List<string> census = [.. slugs
            .Where(slug => !TextNormalization.IsTestSlug(slug))
            .Distinct(StringComparer.Ordinal)];
        if (census.Count == 0)
        {
            return new PlatformCensusResult(0, 0, []);
        }

        string stamp = UtcStamp();
        using SqliteTransaction tx = conn.BeginTransaction();

        // Existing slug → ACTIVE, loaded once (one query instead of one per slug).
        Dictionary<string, bool> existing = new(StringComparer.Ordinal);
        using (SqliteCommand load = Command(conn, tx, """
            SELECT SLUG, ACTIVE FROM PLATFORM_LOCATIONS
            WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat;
            """,
            ("$qsr", qsrId), ("$plat", platformId)))
        using (SqliteDataReader reader = load.ExecuteReader())
        {
            while (reader.Read())
            {
                existing[reader.GetString(0)] = reader.GetInt64(1) != 0;
            }
        }

        using SqliteCommand insert = Command(conn, tx, """
            INSERT INTO PLATFORM_LOCATIONS (QSR_ID, PLATFORM_ID, SLUG, ACTIVE, LAST_SEEN_AT)
            VALUES ($qsr, $plat, $slug, 1, $seen);
            """,
            ("$qsr", qsrId), ("$plat", platformId), ("$seen", stamp));
        SqliteParameter insertSlug = insert.Parameters.Add("$slug", SqliteType.Text);

        using SqliteCommand touch = Command(conn, tx, """
            UPDATE PLATFORM_LOCATIONS SET ACTIVE = 1, LAST_SEEN_AT = $seen
            WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat AND SLUG = $slug;
            """,
            ("$qsr", qsrId), ("$plat", platformId), ("$seen", stamp));
        SqliteParameter touchSlug = touch.Parameters.Add("$slug", SqliteType.Text);

        int inserted = 0, reactivated = 0;
        HashSet<string> censusSet = new(census, StringComparer.Ordinal);
        foreach (string slug in census)
        {
            if (existing.TryGetValue(slug, out bool wasActive))
            {
                touchSlug.Value = slug;
                touch.ExecuteNonQuery();
                if (!wasActive)
                {
                    reactivated++;
                }
            }
            else
            {
                insertSlug.Value = slug;
                insert.ExecuteNonQuery();
                inserted++;
            }
        }

        // Active rows the census no longer lists → soft delete.
        List<string> delisted = [];
        using SqliteCommand retire = Command(conn, tx, """
            UPDATE PLATFORM_LOCATIONS SET ACTIVE = 0
            WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat AND SLUG = $slug;
            """,
            ("$qsr", qsrId), ("$plat", platformId));
        SqliteParameter retireSlug = retire.Parameters.Add("$slug", SqliteType.Text);
        foreach ((string slug, bool wasActive) in existing)
        {
            if (wasActive && !censusSet.Contains(slug))
            {
                retireSlug.Value = slug;
                retire.ExecuteNonQuery();
                delisted.Add(slug);
            }
        }

        tx.Commit();
        return new PlatformCensusResult(inserted, reactivated, delisted);
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
        List<string> setClauses = [];
        List<(string Name, object? Value)> parameters = [];

        if (name is not null)
        {
            setClauses.Add("NAME = $name");
            parameters.Add(("$name", TextNormalization.Title(name)));
        }

        if (address is not null)
        {
            string? newAddress = TextNormalization.Title(address);
            string? existingAddress = Scalar(conn,
                "SELECT ADDRESS FROM PLATFORM_LOCATIONS WHERE ID = $id;",
                ("$id", platformLocationId)) as string;

            setClauses.Add("ADDRESS = $addr");
            parameters.Add(("$addr", newAddress));

            // ADDRESS changed → coordinates are stale; force a re-geocode.
            if (existingAddress != newAddress)
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
        {
            return;
        }

        parameters.Add(("$id", platformLocationId));
        NonQuery(conn,
            $"UPDATE PLATFORM_LOCATIONS SET {string.Join(", ", setClauses)} WHERE ID = $id;",
            [.. parameters]);
    }

    public static void SetPlatformLocationMatch(SqliteConnection conn, long platformLocationId, long? locationId)
    {
        NonQuery(conn,
            "UPDATE PLATFORM_LOCATIONS SET LOCATION_ID = $loc WHERE ID = $id;",
            ("$loc", locationId), ("$id", platformLocationId));
    }

    // ── MENU_ITEMS ───────────────────────────────────────────────────────────

    public static long GetOrCreateMenuItem(SqliteConnection conn, long qsrId, string name)
    {
        return GetOrCreateId(conn,
            "SELECT ID FROM MENU_ITEMS WHERE QSR_ID = $qsr AND NAME = $name;",
            "INSERT INTO MENU_ITEMS (QSR_ID, NAME) VALUES ($qsr, $name); SELECT last_insert_rowid();",
            ("$qsr", qsrId), ("$name", name));
    }

    // ── SCRAPE_RUNS / MENU_SNAPSHOTS ─────────────────────────────────────────

    public static long StartScrapeRun(SqliteConnection conn, long qsrId)
    {
        return Convert.ToInt64(Scalar(conn, """
            INSERT INTO SCRAPE_RUNS (QSR_ID, STARTED_AT) VALUES ($qsr, $started);
            SELECT last_insert_rowid();
            """,
            ("$qsr", qsrId), ("$started", UtcStamp())), CultureInfo.InvariantCulture);
    }

    public static void FinishScrapeRun(SqliteConnection conn, long runId)
    {
        NonQuery(conn,
            "UPDATE SCRAPE_RUNS SET FINISHED_AT = $finished WHERE ID = $id;",
            ("$finished", UtcStamp()), ("$id", runId));
    }

    public static void InsertSnapshot(
        SqliteConnection conn, long runId, long platformLocationId, long menuItemId, decimal price)
    {
        NonQuery(conn, """
            INSERT INTO MENU_SNAPSHOTS (SCRAPE_RUN_ID, PLATFORM_LOCATION_ID, MENU_ITEM_ID, PRICE)
            VALUES ($run, $pl, $item, $price);
            """,
            ("$run", runId), ("$pl", platformLocationId), ("$item", menuItemId), ("$price", price));
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

        using SqliteCommand cmd = Command(conn, """
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
              AND pl.ACTIVE = 1  -- delisted pages are skipped by design, not "unscraped"
              AND NOT EXISTS (
                SELECT 1 FROM MENU_SNAPSHOTS ms
                WHERE ms.PLATFORM_LOCATION_ID = pl.ID
                  AND ms.SCRAPE_RUN_ID        = lr.RUN_ID
            )
            ORDER BY pl.SLUG;
            """,
            ("$qsr", qsrId));

        List<UnscrapedLocationRow> result = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string qsrName = reader.GetString(0);
            string? name = reader.IsDBNull(1) ? null : reader.GetString(1);
            string? address = reader.IsDBNull(2) ? null : reader.GetString(2);
            string slug = reader.GetString(3);

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
        using SqliteCommand cmd = Command(conn, """
            SELECT pr.NAME, di.NAME, COUNT(*)
            FROM LOCATIONS l
            JOIN DISTRICTS di ON di.ID = l.DISTRICT_ID
            JOIN PROVINCES pr ON pr.ID = di.PROVINCE_ID
            WHERE l.QSR_ID = $qsr
              AND l.ACTIVE = 1
            GROUP BY pr.NAME, di.NAME
            ORDER BY pr.NAME, di.NAME;
            """,
            ("$qsr", qsrId));

        List<ProvinceDistrictCount> result = [];
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
        string sql = activeOnly
            ? "SELECT COUNT(*) FROM LOCATIONS WHERE QSR_ID = $qsr AND ACTIVE = 1;"
            : "SELECT COUNT(*) FROM LOCATIONS WHERE QSR_ID = $qsr;";
        object? scalar = Scalar(conn, sql, ("$qsr", qsrId));
        return scalar is null ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    public static List<ScrapeRunRow> ListScrapeRuns(SqliteConnection conn)
    {
        using SqliteCommand cmd = Command(conn,
            "SELECT ID, QSR_ID, STARTED_AT, FINISHED_AT FROM SCRAPE_RUNS ORDER BY ID;");

        List<ScrapeRunRow> result = [];
        using SqliteDataReader reader = cmd.ExecuteReader();
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
        long qsrId;
        string startedAt;
        using (SqliteCommand cmd = Command(conn,
            "SELECT QSR_ID, STARTED_AT FROM SCRAPE_RUNS WHERE ID = $id;", ("$id", runId)))
        using (SqliteDataReader reader = cmd.ExecuteReader())
        {
            if (!reader.Read())
            {
                return (false, 0, "", 0);
            }
            qsrId = reader.GetInt64(0);
            startedAt = reader.GetString(1);
        }

        int count = Convert.ToInt32(Scalar(conn,
            "SELECT COUNT(*) FROM MENU_SNAPSHOTS WHERE SCRAPE_RUN_ID = $id;", ("$id", runId)), CultureInfo.InvariantCulture);

        return (true, qsrId, startedAt, count);
    }

    public static void DeleteScrapeRun(SqliteConnection conn, long runId)
    {
        using SqliteTransaction tx = conn.BeginTransaction();
        using (SqliteCommand deleteSnapshots = Command(conn, tx,
            "DELETE FROM MENU_SNAPSHOTS WHERE SCRAPE_RUN_ID = $id;", ("$id", runId)))
        {
            deleteSnapshots.ExecuteNonQuery();
        }
        using (SqliteCommand deleteRun = Command(conn, tx,
            "DELETE FROM SCRAPE_RUNS WHERE ID = $id;", ("$id", runId)))
        {
            deleteRun.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public static int DeleteScrapeRunsBefore(SqliteConnection conn, long runId)
    {
        using SqliteTransaction tx = conn.BeginTransaction();

        int affected;
        using (SqliteCommand deleteSnapshots = Command(conn, tx, """
            DELETE FROM MENU_SNAPSHOTS
            WHERE SCRAPE_RUN_ID IN (SELECT ID FROM SCRAPE_RUNS WHERE ID < $id);
            """, ("$id", runId)))
        {
            deleteSnapshots.ExecuteNonQuery();
        }
        using (SqliteCommand deleteRuns = Command(conn, tx,
            "DELETE FROM SCRAPE_RUNS WHERE ID < $id;", ("$id", runId)))
        {
            affected = deleteRuns.ExecuteNonQuery();
        }
        tx.Commit();
        return affected;
    }

    // ── Logo blobs ───────────────────────────────────────────────────────────

    /// <summary>Return the stored PNG/SVG logo bytes for a QSR row, or null if none set.</summary>
    public static byte[]? GetLogoBlob(SqliteConnection conn, long qsrId)
    {
        object? result = Scalar(conn, "SELECT LOGO_BLOB FROM QSR WHERE ID = $id;", ("$id", qsrId));
        return result is DBNull or null ? null : (byte[])result;
    }

    public static void SetLogoBlob(SqliteConnection conn, long qsrId, byte[] logoBytes)
    {
        NonQuery(conn,
            "UPDATE QSR SET LOGO_BLOB = $blob WHERE ID = $id;",
            ("$blob", logoBytes), ("$id", qsrId));
    }
}
