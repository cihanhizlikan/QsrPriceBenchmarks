using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Models;

namespace QsrPriceBenchmarks.Core.Db;

/// <summary>
/// Opens (creating if necessary) the SQLite database, applies the schema,
/// seeds reference data (QSR / PLATFORMS / QSR_PLATFORMS / SCRAPE_TABS from
/// the chain registry), and (re)creates the reporting views.
///
/// Schema notes — these mirror specific bugs found and fixed during the
/// Python prototype's development; do not "simplify" them without re-reading
/// why they're here:
///   - LOCATIONS unique key is (QSR_ID, SLUG), not SLUG alone — the same
///     district/restaurant slug can legitimately repeat across chains.
///   - PLATFORM_LOCATIONS unique key is (QSR_ID, PLATFORM_ID, SLUG), not
///     (PLATFORM_ID, SLUG) — two different chains' restaurants can share the
///     same TG slug (e.g. same shopping mall).
///   - Views are DROP + CREATE on every open, not CREATE IF NOT EXISTS —
///     so that schema/view bugfixes always reach existing databases without
///     requiring the user to manually drop them.
/// </summary>
public static class Database
{
    public static SqliteConnection Open(string path)
    {
        // SQLite creates a missing database *file* but never a missing parent
        // *directory* — that's the usual cause of SQLITE_CANTOPEN. Create the
        // directory first so opening a path in a fresh location (e.g. the
        // per-user app-data folder) succeeds. Skipped for special in-memory
        // sources (":memory:", "file::memory:…") which have no real directory.
        if (!path.StartsWith(":", StringComparison.Ordinal) && !path.Contains(":memory:"))
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        SqliteConnection conn = new($"Data Source={path}");
        conn.Open();

        try
        {
            // foreign_keys: enforce REFERENCES at write time.
            // journal_mode=WAL: let readers (e.g. an Excel export) run without
            // being blocked by the pipeline's writes, so completed runs can be
            // exported/deleted while another scrape run is in progress.
            // busy_timeout: when two writers contend (e.g. a delete during a
            // scrape), wait for the lock instead of throwing SQLITE_BUSY.
            Execute(conn, "PRAGMA foreign_keys = ON;");
            Execute(conn, "PRAGMA journal_mode = WAL;");
            Execute(conn, "PRAGMA busy_timeout = 10000;");

            ApplySchema(conn);
            MigrateSchema(conn);
            SeedReferenceData(conn);
            CreateViews(conn);

            return conn;
        }
        catch
        {
            // Never leak the open connection (and its file handle / lock) when
            // schema setup fails partway through.
            conn.Dispose();
            throw;
        }
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void ApplySchema(SqliteConnection conn)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS QSR (
                ID       INTEGER PRIMARY KEY AUTOINCREMENT,
                NAME     TEXT    NOT NULL UNIQUE,
                BASE_URL TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS PLATFORMS (
                ID       INTEGER PRIMARY KEY AUTOINCREMENT,
                NAME     TEXT    NOT NULL UNIQUE,
                BASE_URL TEXT    NOT NULL
            );

            CREATE TABLE IF NOT EXISTS QSR_PLATFORMS (
                QSR_ID      INTEGER NOT NULL REFERENCES QSR(ID),
                PLATFORM_ID INTEGER NOT NULL REFERENCES PLATFORMS(ID),
                SLUG        TEXT    NOT NULL,
                PRIMARY KEY (QSR_ID, PLATFORM_ID)
            );

            CREATE TABLE IF NOT EXISTS SCRAPE_TABS (
                ID            INTEGER PRIMARY KEY AUTOINCREMENT,
                QSR_ID        INTEGER NOT NULL REFERENCES QSR(ID),
                PLATFORM_ID   INTEGER NOT NULL REFERENCES PLATFORMS(ID),
                TAB_NAME      TEXT    NOT NULL,
                DISPLAY_ORDER INTEGER NOT NULL,
                UNIQUE (QSR_ID, PLATFORM_ID, TAB_NAME)
            );

            CREATE TABLE IF NOT EXISTS PROVINCES (
                ID   INTEGER PRIMARY KEY AUTOINCREMENT,
                NAME TEXT NOT NULL UNIQUE
            );

            CREATE TABLE IF NOT EXISTS DISTRICTS (
                ID      INTEGER PRIMARY KEY AUTOINCREMENT,
                PROVINCE_ID INTEGER NOT NULL REFERENCES PROVINCES(ID),
                NAME    TEXT    NOT NULL,
                UNIQUE (PROVINCE_ID, NAME)
            );

            CREATE TABLE IF NOT EXISTS LOCATIONS (
                ID          INTEGER PRIMARY KEY AUTOINCREMENT,
                QSR_ID      INTEGER NOT NULL REFERENCES QSR(ID),
                DISTRICT_ID INTEGER REFERENCES DISTRICTS(ID),
                SLUG        TEXT    NOT NULL,
                NAME        TEXT,
                ADDRESS     TEXT,
                LATITUDE    REAL,
                LONGITUDE   REAL,
                ACTIVE       INTEGER NOT NULL DEFAULT 1,
                LAST_SEEN_AT TEXT,
                UNIQUE (QSR_ID, SLUG)
            );

            CREATE TABLE IF NOT EXISTS PLATFORM_LOCATIONS (
                ID          INTEGER PRIMARY KEY AUTOINCREMENT,
                QSR_ID      INTEGER NOT NULL REFERENCES QSR(ID),
                PLATFORM_ID INTEGER NOT NULL REFERENCES PLATFORMS(ID),
                LOCATION_ID INTEGER REFERENCES LOCATIONS(ID),
                SLUG        TEXT    NOT NULL,
                NAME        TEXT,
                ADDRESS     TEXT,
                LATITUDE    REAL,
                LONGITUDE   REAL,
                UNIQUE (QSR_ID, PLATFORM_ID, SLUG)
            );

            CREATE TABLE IF NOT EXISTS MENU_ITEMS (
                ID     INTEGER PRIMARY KEY AUTOINCREMENT,
                QSR_ID INTEGER NOT NULL REFERENCES QSR(ID),
                NAME   TEXT    NOT NULL,
                UNIQUE (QSR_ID, NAME)
            );

            CREATE TABLE IF NOT EXISTS SCRAPE_RUNS (
                ID          INTEGER PRIMARY KEY AUTOINCREMENT,
                QSR_ID      INTEGER NOT NULL REFERENCES QSR(ID),
                STARTED_AT  TEXT    NOT NULL,
                FINISHED_AT TEXT
            );

            CREATE TABLE IF NOT EXISTS MENU_SNAPSHOTS (
                ID                   INTEGER PRIMARY KEY AUTOINCREMENT,
                SCRAPE_RUN_ID        INTEGER NOT NULL REFERENCES SCRAPE_RUNS(ID),
                PLATFORM_LOCATION_ID INTEGER NOT NULL REFERENCES PLATFORM_LOCATIONS(ID),
                MENU_ITEM_ID         INTEGER NOT NULL REFERENCES MENU_ITEMS(ID),
                PRICE                REAL    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_LOCATIONS_QSR            ON LOCATIONS(QSR_ID);
            CREATE INDEX IF NOT EXISTS IX_PLATFORM_LOCATIONS_QSR   ON PLATFORM_LOCATIONS(QSR_ID, PLATFORM_ID);
            CREATE INDEX IF NOT EXISTS IX_PLATFORM_LOCATIONS_LOC   ON PLATFORM_LOCATIONS(LOCATION_ID);
            CREATE INDEX IF NOT EXISTS IX_MENU_SNAPSHOTS_RUN       ON MENU_SNAPSHOTS(SCRAPE_RUN_ID);
            CREATE INDEX IF NOT EXISTS IX_MENU_SNAPSHOTS_PL        ON MENU_SNAPSHOTS(PLATFORM_LOCATION_ID);
            CREATE INDEX IF NOT EXISTS IX_SCRAPE_RUNS_QSR          ON SCRAPE_RUNS(QSR_ID);
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Additive schema migrations applied on every open so existing databases
    /// pick up new columns without requiring a manual ALTER TABLE.
    /// Uses PRAGMA table_info rather than try/catch to stay idiomatic.
    /// </summary>
    private static void MigrateSchema(SqliteConnection conn)
    {
        // LOGO_BLOB: nullable PNG/SVG bytes for each chain (UI chain-selector logos).
        if (!ColumnExists(conn, "QSR", "LOGO_BLOB"))
            Execute(conn, "ALTER TABLE QSR ADD COLUMN LOGO_BLOB BLOB;");

        // Rename the chain formerly seeded as "Usta Döner" to its correct name
        // "Usta Dönerci", preserving its QSR row and every FK-linked location,
        // platform location, and scrape run. Idempotent (matches nothing once
        // renamed) and must run before SeedReferenceData, whose GetOrCreate keys
        // QSR on NAME and would otherwise create a second, empty row.
        Execute(conn, "UPDATE QSR SET NAME = 'Usta Dönerci' WHERE NAME = 'Usta Döner';");

        // Soft-delete support for Step 1: ACTIVE flags whether a location is still
        // listed on the chain's site; LAST_SEEN_AT records the last crawl that saw
        // it. Existing rows default to ACTIVE = 1 (treated as open until the next
        // crawl reconciles them). LOCATIONS are never hard-deleted, so price
        // history and PLATFORM_LOCATIONS matches survive a closure-and-reopen.
        if (!ColumnExists(conn, "LOCATIONS", "ACTIVE"))
        {
            Execute(conn, "ALTER TABLE LOCATIONS ADD COLUMN ACTIVE INTEGER NOT NULL DEFAULT 1;");
        }
        if (!ColumnExists(conn, "LOCATIONS", "LAST_SEEN_AT"))
        {
            Execute(conn, "ALTER TABLE LOCATIONS ADD COLUMN LAST_SEEN_AT TEXT;");
        }
    }

    /// <summary>True when <paramref name="table"/> has a column named <paramref name="column"/>.</summary>
    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        using var cmd = conn.CreateCommand();
        // table_info() takes an identifier, not a bindable parameter; table names
        // here are compile-time constants, never user input.
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
                return true;
        return false;
    }

    private static void SeedReferenceData(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();

        // Single TG platform shared by all chains.
        long platformId = GetOrCreate(conn, tx, "PLATFORMS",
            new() { ["NAME"] = "Tıkla Gelsin" },
            new() { ["NAME"] = "Tıkla Gelsin", ["BASE_URL"] = "https://www.tiklagelsin.com/restoran/" });

        // Insert chains alphabetically so a freshly-created DB assigns QSR IDs
        // in A→Z order (Arby's = 1, Burger King = 2, …). Ordinal keeps the order
        // deterministic across machine locales; for the current names it matches
        // the alphabetical sidebar order. Existing rows keep their IDs (GetOrCreate
        // matches on NAME), so this only affects DBs built from scratch.
        foreach (var chain in Models.Chains.All.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            long qsrId = GetOrCreate(conn, tx, "QSR",
                new() { ["NAME"] = chain.Name },
                new() { ["NAME"] = chain.Name, ["BASE_URL"] = chain.BaseUrl ?? "" });

            foreach (var (platformName, slug) in chain.PlatformSlugs)
            {
                // Only one platform exists today (Tıkla Gelsin); resolved above.
                using var qp = conn.CreateCommand();
                qp.Transaction = tx;
                qp.CommandText = """
                    INSERT INTO QSR_PLATFORMS (QSR_ID, PLATFORM_ID, SLUG)
                    VALUES ($qsr, $plat, $slug)
                    ON CONFLICT (QSR_ID, PLATFORM_ID) DO UPDATE SET SLUG = excluded.SLUG;
                    """;
                qp.Parameters.AddWithValue("$qsr", qsrId);
                qp.Parameters.AddWithValue("$plat", platformId);
                qp.Parameters.AddWithValue("$slug", slug);
                qp.ExecuteNonQuery();

                if (chain.ScrapeTabs.TryGetValue(platformName, out var tabs))
                {
                    // Authoritative sync: SCRAPE_TABS is config derived entirely
                    // from ChainConfig, so make the DB match the config exactly
                    // on every Open — clear this chain/platform's tabs, then
                    // re-insert the current list in order. This removes tabs that
                    // were dropped from the config (e.g. a renamed TG tab) and
                    // fixes DISPLAY_ORDER, which a plain INSERT OR IGNORE could not.
                    using (var del = conn.CreateCommand())
                    {
                        del.Transaction = tx;
                        del.CommandText =
                            "DELETE FROM SCRAPE_TABS WHERE QSR_ID = $qsr AND PLATFORM_ID = $plat;";
                        del.Parameters.AddWithValue("$qsr", qsrId);
                        del.Parameters.AddWithValue("$plat", platformId);
                        del.ExecuteNonQuery();
                    }

                    for (int i = 0; i < tabs.Count; i++)
                    {
                        using var st = conn.CreateCommand();
                        st.Transaction = tx;
                        st.CommandText = """
                            INSERT INTO SCRAPE_TABS
                                (QSR_ID, PLATFORM_ID, TAB_NAME, DISPLAY_ORDER)
                            VALUES ($qsr, $plat, $tab, $ord);
                            """;
                        st.Parameters.AddWithValue("$qsr", qsrId);
                        st.Parameters.AddWithValue("$plat", platformId);
                        st.Parameters.AddWithValue("$tab", tabs[i]);
                        st.Parameters.AddWithValue("$ord", i);
                        st.ExecuteNonQuery();
                    }
                }
            }
        }

        tx.Commit();
    }

    /// <summary>
    /// Find-or-insert helper: looks up a row by <paramref name="lookup"/>
    /// columns; if absent, inserts using <paramref name="insertValues"/>.
    /// Returns the row's ID either way.
    /// </summary>
    private static long GetOrCreate(
        SqliteConnection conn, SqliteTransaction tx, string table,
        Dictionary<string, object> lookup, Dictionary<string, object> insertValues)
    {
        using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            var whereClauses = lookup.Keys.Select(k => $"{k} = ${k}");
            find.CommandText = $"SELECT ID FROM {table} WHERE {string.Join(" AND ", whereClauses)};";
            foreach (var (k, v) in lookup)
                find.Parameters.AddWithValue($"${k}", v);

            var existing = find.ExecuteScalar();
            if (existing is not null && existing is not DBNull)
                return Convert.ToInt64(existing);
        }

        using (var insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            var cols = insertValues.Keys.ToList();
            var paramNames = cols.Select(c => $"${c}");
            insert.CommandText =
                $"INSERT INTO {table} ({string.Join(", ", cols)}) " +
                $"VALUES ({string.Join(", ", paramNames)}); " +
                "SELECT last_insert_rowid();";
            foreach (var (k, v) in insertValues)
                insert.Parameters.AddWithValue($"${k}", v);

            return Convert.ToInt64(insert.ExecuteScalar());
        }
    }

    private static void CreateViews(SqliteConnection conn)
    {
        const string ddl = """
            DROP VIEW IF EXISTS V_LATEST_PRICES;
            DROP VIEW IF EXISTS V_PRICE_BENCHMARKS;
            DROP VIEW IF EXISTS V_MARKETING_REPORT;

            -- Latest snapshot price per (platform_location, menu_item), by the
            -- highest SCRAPE_RUN_ID seen for that pair.
            CREATE VIEW V_LATEST_PRICES AS
            WITH latest AS (
                SELECT
                    ms.PLATFORM_LOCATION_ID,
                    ms.MENU_ITEM_ID,
                    ms.PRICE                AS PRICE,
                    sr.STARTED_AT            AS SNAPSHOT_TIMESTAMP,
                    MAX(ms.SCRAPE_RUN_ID)    AS SCRAPE_RUN_ID
                FROM MENU_SNAPSHOTS ms
                JOIN SCRAPE_RUNS sr ON ms.SCRAPE_RUN_ID = sr.ID
                GROUP BY ms.PLATFORM_LOCATION_ID, ms.MENU_ITEM_ID
            )
            SELECT
                pt.QSR_ID                AS QSR_ID,
                l.PRICE                  AS PRICE,
                l.SNAPSHOT_TIMESTAMP     AS SNAPSHOT_TIMESTAMP,
                l.SCRAPE_RUN_ID          AS SCRAPE_RUN_ID,
                pt.ID                    AS PLATFORM_LOCATION_ID,
                mi.ID                    AS MENU_ITEM_ID,
                mi.NAME                  AS MENU_ITEM_NAME,
                pt.NAME                  AS PLATFORM_LOCATION_NAME
            FROM latest l
            JOIN PLATFORM_LOCATIONS pt ON pt.ID = l.PLATFORM_LOCATION_ID
            JOIN MENU_ITEMS         mi ON mi.ID = l.MENU_ITEM_ID;

            -- Matched-location price comparison: only rows where the platform
            -- listing has been matched to a physical LOCATIONS row.
            CREATE VIEW V_PRICE_BENCHMARKS AS
            SELECT
                q.NAME                   AS QSR,
                c.NAME                   AS PROVINCE,
                p.NAME                   AS DISTRICT,
                loc.NAME                 AS LOCATION_NAME,
                loc.ADDRESS              AS LOCATION_ADDRESS,
                lp.MENU_ITEM_NAME        AS MENU_ITEM,
                lp.PRICE                 AS PRICE,
                lp.SNAPSHOT_TIMESTAMP    AS SNAPSHOT_TIMESTAMP
            FROM V_LATEST_PRICES lp
            JOIN PLATFORM_LOCATIONS pt ON pt.ID = lp.PLATFORM_LOCATION_ID
            JOIN LOCATIONS          loc ON pt.LOCATION_ID = loc.ID
            JOIN QSR                q   ON loc.QSR_ID = q.ID
            LEFT JOIN DISTRICTS     p   ON loc.DISTRICT_ID = p.ID
            LEFT JOIN PROVINCES     c   ON p.PROVINCE_ID = c.ID;

            -- Full marketing report: current price, previous price, change,
            -- and recency, per (platform_location, menu_item).
            CREATE VIEW V_MARKETING_REPORT AS
            WITH latest AS (
                SELECT
                    ms.PLATFORM_LOCATION_ID,
                    ms.MENU_ITEM_ID,
                    ms.PRICE                                AS CURRENT_PRICE,
                    sr.STARTED_AT                            AS SNAPSHOT_TIMESTAMP,
                    MAX(ms.SCRAPE_RUN_ID)                    AS _latest_run_id
                FROM MENU_SNAPSHOTS ms
                JOIN SCRAPE_RUNS sr ON ms.SCRAPE_RUN_ID = sr.ID
                GROUP BY ms.PLATFORM_LOCATION_ID, ms.MENU_ITEM_ID
            ),
            prev AS (
                SELECT
                    ms.PLATFORM_LOCATION_ID,
                    ms.MENU_ITEM_ID,
                    ms.PRICE                                AS PREV_PRICE,
                    MAX(ms.SCRAPE_RUN_ID)                    AS _prev_run_id
                FROM MENU_SNAPSHOTS ms
                JOIN latest l ON ms.PLATFORM_LOCATION_ID = l.PLATFORM_LOCATION_ID
                             AND ms.MENU_ITEM_ID         = l.MENU_ITEM_ID
                             AND ms.SCRAPE_RUN_ID        < l._latest_run_id
                GROUP BY ms.PLATFORM_LOCATION_ID, ms.MENU_ITEM_ID
            )
            SELECT
                sr.QSR_ID                                   AS QSR_ID,
                q.NAME                                      AS QSR,
                c.NAME                                      AS PROVINCE,
                p.NAME                                      AS DISTRICT,
                l.NAME                                      AS LOCATION_NAME,
                l.ADDRESS                                   AS LOCATION_ADDRESS,
                pl.NAME                                     AS PLATFORM,
                pt.NAME                                      AS PLATFORM_LOCATION_NAME,
                pt.ADDRESS                                   AS PLATFORM_LOCATION_ADDRESS,
                pl.BASE_URL || COALESCE(qp2.SLUG,'') || '/' || pt.SLUG || '?gel-al'
                                                              AS PLATFORM_URL,
                mi.NAME                                      AS MENU_ITEM,
                lp.CURRENT_PRICE                             AS PRICE,
                pv.PREV_PRICE                                AS PREV_PRICE,
                ROUND(lp.CURRENT_PRICE - COALESCE(pv.PREV_PRICE, lp.CURRENT_PRICE), 2)
                                                              AS PRICE_CHANGE,
                sr.ID                                        AS SCRAPE_RUN_ID,
                lp.SNAPSHOT_TIMESTAMP                        AS LAST_SCRAPE_RUN,
                CAST(julianday(date('now')) -
                     julianday(date(REPLACE(lp.SNAPSHOT_TIMESTAMP, 'T', ' ')))
                     AS INTEGER)                             AS DAYS_SINCE_SCRAPE
            FROM latest                          lp
            JOIN SCRAPE_RUNS                     sr  ON sr.ID                    = lp._latest_run_id
            JOIN PLATFORM_LOCATIONS               pt  ON lp.PLATFORM_LOCATION_ID  = pt.ID
            JOIN PLATFORMS                        pl  ON pt.PLATFORM_ID           = pl.ID
            JOIN MENU_ITEMS                       mi  ON lp.MENU_ITEM_ID          = mi.ID
            LEFT JOIN prev                        pv  ON pv.PLATFORM_LOCATION_ID  = lp.PLATFORM_LOCATION_ID
                                                       AND pv.MENU_ITEM_ID         = lp.MENU_ITEM_ID
            LEFT JOIN LOCATIONS                   l   ON pt.LOCATION_ID           = l.ID
            JOIN QSR                              q   ON q.ID                     = sr.QSR_ID
            LEFT JOIN QSR_PLATFORMS               qp2 ON qp2.QSR_ID               = pt.QSR_ID
                                                       AND qp2.PLATFORM_ID         = pt.PLATFORM_ID
            LEFT JOIN DISTRICTS                   p   ON l.DISTRICT_ID            = p.ID
            LEFT JOIN PROVINCES                   c   ON p.PROVINCE_ID            = c.ID;
            """;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        cmd.ExecuteNonQuery();
    }
}
