using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
    /// <summary>File name used when the user picks a folder rather than a file.</summary>
    public const string DefaultFileName = "QsrPriceBenchmarks.sqlite";

    // Tables that identify a database as ours: QSR is in every schema version,
    // CITIES only in pre-v1.1 legacy files (see MigrateLegacySchema).
    private static readonly string[] RecognisedTables = ["QSR", "CITIES"];

    public static SqliteConnection Open(string path)
    {
        // SQLite creates a missing database *file* but never a missing parent
        // *directory* — that's the usual cause of SQLITE_CANTOPEN. Create the
        // directory first so opening a path in a fresh location (e.g. the
        // per-user app-data folder) succeeds. Skipped for special in-memory
        // sources (":memory:", "file::memory:…") which have no real directory.
        if (!path.StartsWith(":", StringComparison.Ordinal) && !path.Contains(":memory:", StringComparison.Ordinal))
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
            // busy_timeout first: it is per-connection (never written to the
            // file, so safe even before the foreign-database guard) and it
            // covers every statement below — including the guard itself, which
            // would otherwise hit a transient SQLITE_BUSY when another instance
            // of the app is mid-write in the same database.
            Execute(conn, "PRAGMA busy_timeout = 10000;");

            // Refuse to touch a foreign SQLite database — checked before the
            // remaining pragmas, because journal_mode=WAL below persistently
            // rewrites the file header and ApplySchema would CREATE our tables
            // inside somebody else's file the moment its path lands in the DB box.
            string? schemaError = ClassifySchema(conn);
            if (schemaError is not null)
            {
                throw new InvalidOperationException(schemaError);
            }

            // foreign_keys: enforce REFERENCES at write time.
            // journal_mode=WAL: let readers (e.g. an Excel export) run without
            // being blocked by the pipeline's writes, so completed runs can be
            // exported/deleted while another scrape run is in progress — and
            // it's also what lets two app instances share one database file.
            Execute(conn, "PRAGMA foreign_keys = ON;");
            Execute(conn, "PRAGMA journal_mode = WAL;");

            // MUST run before ApplySchema: it renames the pre-1.1 geography
            // tables into the current names, and ApplySchema would otherwise
            // create new empty tables under those names first.
            MigrateLegacySchema(conn);

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

    /// <summary>
    /// Pre-flight check for a user-chosen database path, without initialising
    /// or modifying anything. Returns null when the path is usable: the file
    /// doesn't exist yet (it will be created), is empty, is an empty SQLite
    /// database, or already carries this app's schema (current or legacy).
    /// Returns a human-readable error for anything else — a non-SQLite file,
    /// or some other application's SQLite database.
    ///
    /// Never throws. A database that's merely busy (open in another instance —
    /// legal under WAL) must validate fine, so the header is read with
    /// ReadWrite sharing and the probe connection gets a busy timeout; any
    /// genuinely unreadable file comes back as an error string, not a crash.
    /// </summary>
    public static string? ValidateFile(string path)
    {
        try
        {
            if (path.StartsWith(":", StringComparison.Ordinal) || path.Contains(":memory:", StringComparison.Ordinal))
            {
                return null;
            }
            if (!File.Exists(path))
            {
                return null; // Open() creates it
            }
            FileInfo info = new(path);
            if (info.Length == 0)
            {
                return null; // SQLite treats a zero-byte file as a new database
            }

            // SQLite files start with the 16-byte magic "SQLite format 3\0".
            // FileShare.ReadWrite is essential: another running instance keeps
            // the file open for writing, and the default FileShare.Read would
            // throw a sharing-violation IOException just for peeking at it.
            byte[] header = new byte[16];
            using (FileStream stream = new(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int read = stream.Read(header, 0, header.Length);
                if (read < header.Length)
                {
                    return "The file is not an SQLite database.";
                }
            }
            if (!System.Text.Encoding.ASCII.GetString(header, 0, 15).Equals("SQLite format 3", StringComparison.Ordinal))
            {
                return "The file is not an SQLite database.";
            }

            // Mode=ReadWrite (no Create) — we only SELECT from sqlite_master,
            // nothing is initialised or written by this probe. DefaultTimeout
            // maps to SQLite's busy handler, so a moment of write contention
            // from the other instance waits instead of failing.
            using SqliteConnection conn = new($"Data Source={path};Mode=ReadWrite");
            conn.DefaultTimeout = 5;
            conn.Open();
            return ClassifySchema(conn);
        }
        catch (Exception ex)
        {
            return $"The file could not be read ({ex.Message}).";
        }
    }

    /// <summary>
    /// Null when the open database is usable by this app (empty, or contains a
    /// recognised schema — current or legacy); otherwise a description of why
    /// not, listing the foreign tables found.
    /// </summary>
    private static string? ClassifySchema(SqliteConnection conn)
    {
        List<string> tables = [];
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        if (tables.Count == 0)
        {
            return null; // brand-new/empty database — ours to initialise
        }
        foreach (string marker in RecognisedTables)
        {
            if (tables.Contains(marker, StringComparer.OrdinalIgnoreCase))
            {
                return null; // current or legacy QSR Price Benchmarks schema
            }
        }

        string sample = string.Join(", ", tables.Take(5));
        if (tables.Count > 5)
        {
            sample += ", …";
        }
        return $"The file is an SQLite database, but not a QSR Price Benchmarks one (tables found: {sample}).";
    }

    [SuppressMessage("Security", "CA2100", Justification = "Runs schema and migration SQL; every caller passes a compile-time constant.")]
    private static void Execute(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
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
                ACTIVE        INTEGER NOT NULL DEFAULT 1,
                LAST_SEEN_AT  TEXT,
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
                ACTIVE      INTEGER NOT NULL DEFAULT 1,
                LAST_SEEN_AT TEXT,
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

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// One-time structural migration for databases created before v1.1, when
    /// Turkish İl was mis-modelled as CITIES and İlçe as PROVINCES. Renames
    /// them to the current PROVINCES (İl) / DISTRICTS (İlçe) — data, IDs, and
    /// foreign keys all survive (SQLite rewrites REFERENCES on RENAME).
    ///
    /// Without this, an old file opened fine (CREATE IF NOT EXISTS is
    /// satisfied by the same-named old tables) but every query touching
    /// DISTRICT_ID failed at execution — the exact "count works, map stays
    /// empty" symptom, because views are validated on use, not creation.
    ///
    /// Runs BEFORE ApplySchema. Detection keys on the legacy CITIES table;
    /// each step is additionally guarded so a half-migrated or previously
    /// half-opened file (which may have gained an empty new-schema DISTRICTS
    /// table) converges to the current schema instead of colliding.
    /// </summary>
    private static void MigrateLegacySchema(SqliteConnection conn)
    {
        if (!TableExists(conn, "CITIES"))
        {
            return; // current schema (or brand-new file) — nothing to do.
        }

        using SqliteTransaction tx = conn.BeginTransaction();

        // Old views reference the old table names and would block the renames;
        // CreateViews recreates all of them at the end of every Open anyway.
        ExecuteInTx(conn, tx, "DROP VIEW IF EXISTS V_LATEST_PRICES;");
        ExecuteInTx(conn, tx, "DROP VIEW IF EXISTS V_PRICE_BENCHMARKS;");
        ExecuteInTx(conn, tx, "DROP VIEW IF EXISTS V_MARKETING_REPORT;");

        // A previous open with post-rename code ran ApplySchema against this
        // file and left behind an empty new-schema DISTRICTS table. Drop it so
        // the rename below can take its name. (Only ever dropped when empty —
        // a populated DISTRICTS means the file is already current and CITIES
        // is an unexpected leftover, which we then leave strictly alone.)
        if (TableExists(conn, tx, "DISTRICTS") && TableIsEmpty(conn, tx, "DISTRICTS"))
        {
            ExecuteInTx(conn, tx, "DROP TABLE DISTRICTS;");
        }

        // Old PROVINCES is the İlçe table, recognisable by its CITY_ID column.
        if (!TableExists(conn, tx, "DISTRICTS") && ColumnExists(conn, tx, "PROVINCES", "CITY_ID"))
        {
            ExecuteInTx(conn, tx, "ALTER TABLE PROVINCES RENAME TO DISTRICTS;");
            ExecuteInTx(conn, tx, "ALTER TABLE DISTRICTS RENAME COLUMN CITY_ID TO PROVINCE_ID;");
        }

        // With the İlçe table renamed away, CITIES (İl) can take PROVINCES.
        if (!TableExists(conn, tx, "PROVINCES"))
        {
            ExecuteInTx(conn, tx, "ALTER TABLE CITIES RENAME TO PROVINCES;");
        }

        if (ColumnExists(conn, tx, "LOCATIONS", "PROVINCE_ID")
            && !ColumnExists(conn, tx, "LOCATIONS", "DISTRICT_ID"))
        {
            ExecuteInTx(conn, tx, "ALTER TABLE LOCATIONS RENAME COLUMN PROVINCE_ID TO DISTRICT_ID;");
        }

        tx.Commit();
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
        {
            Execute(conn, "ALTER TABLE QSR ADD COLUMN LOGO_BLOB BLOB;");
        }

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

        // Soft-delete + discovery support for Step 2's tab list, mirroring the
        // LOCATIONS pattern: ACTIVE flags whether the tab is still shown on the
        // platform's pages; LAST_SEEN_AT records the last run that saw it. Tabs
        // are never hard-deleted, so a tab TG removes and later restores keeps
        // its identity (and a run's tab provenance stays auditable).
        if (!ColumnExists(conn, "SCRAPE_TABS", "ACTIVE"))
        {
            Execute(conn, "ALTER TABLE SCRAPE_TABS ADD COLUMN ACTIVE INTEGER NOT NULL DEFAULT 1;");
        }
        if (!ColumnExists(conn, "SCRAPE_TABS", "LAST_SEEN_AT"))
        {
            Execute(conn, "ALTER TABLE SCRAPE_TABS ADD COLUMN LAST_SEEN_AT TEXT;");
        }

        // Same lifecycle for the platform's restaurant pages: the TG sitemap is
        // a full census fetched at every Step 2 run, so a slug absent from it is
        // the platform's own statement that the restaurant was delisted. ACTIVE
        // keeps closed pages out of scrape queues, matching, and benchmarks;
        // LAST_SEEN_AT records the last census that contained the slug.
        if (!ColumnExists(conn, "PLATFORM_LOCATIONS", "ACTIVE"))
        {
            Execute(conn, "ALTER TABLE PLATFORM_LOCATIONS ADD COLUMN ACTIVE INTEGER NOT NULL DEFAULT 1;");
        }
        if (!ColumnExists(conn, "PLATFORM_LOCATIONS", "LAST_SEEN_AT"))
        {
            Execute(conn, "ALTER TABLE PLATFORM_LOCATIONS ADD COLUMN LAST_SEEN_AT TEXT;");
        }
    }

    /// <summary>True when a table named <paramref name="table"/> exists.</summary>
    private static bool TableExists(SqliteConnection conn, string table)
    {
        return TableExists(conn, null, table);
    }

    private static bool TableExists(SqliteConnection conn, SqliteTransaction? tx, string table)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", table);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>True when <paramref name="table"/> has zero rows.</summary>
    [SuppressMessage("Security", "CA2100", Justification = "A table name cannot be bound as a parameter; every caller passes a constant name, never user input.")]
    private static bool TableIsEmpty(SqliteConnection conn, SqliteTransaction tx, string table)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // Identifier, not a bindable parameter; table names here are
        // compile-time constants, never user input.
        cmd.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0;
    }

    /// <summary>True when <paramref name="table"/> has a column named <paramref name="column"/>.</summary>
    private static bool ColumnExists(SqliteConnection conn, string table, string column)
    {
        return ColumnExists(conn, null, table, column);
    }

    [SuppressMessage("Security", "CA2100", Justification = "A table name cannot be bound as a parameter; every caller passes a constant name, never user input.")]
    private static bool ColumnExists(SqliteConnection conn, SqliteTransaction? tx, string table, string column)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        // table_info() takes an identifier, not a bindable parameter; table names
        // here are compile-time constants, never user input.
        cmd.CommandText = $"PRAGMA table_info({table});";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    [SuppressMessage("Security", "CA2100", Justification = "Runs schema and migration SQL; every caller passes a compile-time constant.")]
    private static void ExecuteInTx(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void SeedReferenceData(SqliteConnection conn)
    {
        using SqliteTransaction tx = conn.BeginTransaction();

        // Single TG platform shared by all chains.
        long platformId = GetOrCreate(conn, tx, "PLATFORMS",
            new() { ["NAME"] = "Tıkla Gelsin" },
            new() { ["NAME"] = "Tıkla Gelsin", ["BASE_URL"] = "https://www.tiklagelsin.com/restoran/" });

        // Insert chains alphabetically so a freshly-created DB assigns QSR IDs
        // in A→Z order (Arby's = 1, Burger King = 2, …). Ordinal keeps the order
        // deterministic across machine locales; for the current names it matches
        // the alphabetical sidebar order. Existing rows keep their IDs (GetOrCreate
        // matches on NAME), so this only affects DBs built from scratch.
        foreach (ChainConfig chain in Models.ChainRegistry.All.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            long qsrId = GetOrCreate(conn, tx, "QSR",
                new() { ["NAME"] = chain.Name },
                new() { ["NAME"] = chain.Name, ["BASE_URL"] = chain.BaseUrl ?? "" });

            foreach ((_, string slug) in chain.PlatformSlugs)
            {
                // Only one platform exists today (Tıkla Gelsin); resolved above.
                using SqliteCommand qp = conn.CreateCommand();
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

                // SCRAPE_TABS is deliberately NOT seeded here. The tab list is
                // discovered live from the platform's pages by Step 2 (and kept
                // honest by its soft-delete reconciliation), so a hardcoded
                // snapshot would only go stale. A fresh database starts with no
                // tabs; the first Step 2 run discovers them (collecting names,
                // addresses, and coordinates meanwhile) and the next run scrapes.
            }
        }

        tx.Commit();
    }

    /// <summary>
    /// Find-or-insert helper: looks up a row by <paramref name="lookup"/>
    /// columns; if absent, inserts using <paramref name="insertValues"/>.
    /// Returns the row's ID either way.
    /// </summary>
    [SuppressMessage("Security", "CA2100", Justification = "Interpolates table and column names from the migration's own constants; values are bound as parameters.")]
    private static long GetOrCreate(
        SqliteConnection conn, SqliteTransaction tx, string table,
        Dictionary<string, object> lookup, Dictionary<string, object> insertValues)
    {
        using (SqliteCommand find = conn.CreateCommand())
        {
            find.Transaction = tx;
            IEnumerable<string> whereClauses = lookup.Keys.Select(k => $"{k} = ${k}");
            find.CommandText = $"SELECT ID FROM {table} WHERE {string.Join(" AND ", whereClauses)};";
            foreach ((string key, object value) in lookup)
            {
                find.Parameters.AddWithValue($"${key}", value);
            }

            object? existing = find.ExecuteScalar();
            if (existing is not null and not DBNull)
            {
                return Convert.ToInt64(existing, CultureInfo.InvariantCulture);
            }
        }

        using (SqliteCommand insert = conn.CreateCommand())
        {
            insert.Transaction = tx;
            List<string> cols = [.. insertValues.Keys];
            IEnumerable<string> paramNames = cols.Select(c => $"${c}");
            insert.CommandText =
                $"INSERT INTO {table} ({string.Join(", ", cols)}) " +
                $"VALUES ({string.Join(", ", paramNames)}); " +
                "SELECT last_insert_rowid();";
            foreach ((string key, object value) in insertValues)
            {
                insert.Parameters.AddWithValue($"${key}", value);
            }

            return Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
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
            JOIN MENU_ITEMS         mi ON mi.ID = l.MENU_ITEM_ID
            WHERE pt.ACTIVE = 1;  -- delisted platform pages keep their history but leave the reports

            -- Matched-location price comparison: only rows where the platform
            -- listing has been matched to a physical LOCATIONS row.
            CREATE VIEW V_PRICE_BENCHMARKS AS
            SELECT
                q.NAME                   AS QSR,
                pr.NAME                  AS PROVINCE,
                di.NAME                  AS DISTRICT,
                loc.NAME                 AS LOCATION_NAME,
                loc.ADDRESS              AS LOCATION_ADDRESS,
                lp.MENU_ITEM_NAME        AS MENU_ITEM,
                lp.PRICE                 AS PRICE,
                lp.SNAPSHOT_TIMESTAMP    AS SNAPSHOT_TIMESTAMP
            FROM V_LATEST_PRICES lp
            JOIN PLATFORM_LOCATIONS pt ON pt.ID = lp.PLATFORM_LOCATION_ID
            JOIN LOCATIONS          loc ON pt.LOCATION_ID = loc.ID
            JOIN QSR                q   ON loc.QSR_ID = q.ID
            LEFT JOIN DISTRICTS     di  ON loc.DISTRICT_ID = di.ID
            LEFT JOIN PROVINCES     pr  ON di.PROVINCE_ID = pr.ID;

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
                pr.NAME                                     AS PROVINCE,
                di.NAME                                     AS DISTRICT,
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
            LEFT JOIN DISTRICTS                   di  ON l.DISTRICT_ID             = di.ID
            LEFT JOIN PROVINCES                   pr  ON di.PROVINCE_ID            = pr.ID
            WHERE pt.ACTIVE = 1;
            """;

        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = ddl;
        cmd.ExecuteNonQuery();
    }
}
