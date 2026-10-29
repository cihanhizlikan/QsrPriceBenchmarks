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
///     so that view bugfixes always reach existing databases without
///     requiring the user to manually drop them.
///
/// Tables are never migrated (maintainer, 2026-10-29: the database is disposable). A new file is stamped with
/// <see cref="SchemaVersion"/>; a file of any other version is refused before anything is written to it, and an
/// older one can be set aside (<see cref="SetAside"/>) so a fresh database takes its place.
/// </summary>
public static class Database
{
    /// <summary>File name used when the user picks a folder rather than a file.</summary>
    public const string DefaultFileName = "QsrPriceBenchmarks.sqlite";

    /// <summary>
    /// The shape <see cref="ApplySchema"/> creates, stamped into every new file as <c>PRAGMA user_version</c>. Bump it
    /// with every change to the tables: files of another version are refused, never migrated. Every file made before
    /// the stamp existed reads 0.
    /// </summary>
    public const int SchemaVersion = 2; // 2: QSR.LOGO_BLOB dropped with the logo feature (WP-D1)

    // The table that identifies a database as this app's, in every version.
    private const string MarkerTable = "QSR";

    // Files this process has already seeded and given fresh views. Doing it on every open took the write lock
    // each time, and several chains scraping at once open the database constantly; once per file per process
    // still lets a view fix reach every existing file the first time a new version opens it.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Prepared =
        new(StringComparer.OrdinalIgnoreCase);

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

            // Refuse a foreign SQLite database, or one of ours from another schema version — checked before the
            // remaining pragmas, because journal_mode=WAL below persistently rewrites the file header and
            // ApplySchema would CREATE our tables inside somebody else's file the moment its path lands in the DB
            // box. A refused file is left byte for byte as it was.
            DatabaseProblem? problem = Classify(conn, path, out bool isNew);
            if (problem is { OlderVersion: int older })
            {
                throw new OutdatedDatabaseException(problem.Message, Path.GetFullPath(path), older);
            }
            if (problem is not null)
            {
                throw new InvalidOperationException(problem.Message);
            }

            // foreign_keys: enforce REFERENCES at write time.
            // journal_mode=WAL: let readers (e.g. an Excel export) run without
            // being blocked by the pipeline's writes, so completed runs can be
            // exported/deleted while another scrape run is in progress — and
            // it's also what lets two app instances share one database file.
            Execute(conn, "PRAGMA foreign_keys = ON;");
            Execute(conn, "PRAGMA journal_mode = WAL;");

            if (isNew)
            {
                ApplySchema(conn);
            }
            string file = conn.DataSource;
            if (isNew || string.IsNullOrEmpty(file) || !Prepared.ContainsKey(file))
            {
                SeedReferenceData(conn);
                CreateViews(conn);
                if (!string.IsNullOrEmpty(file))
                {
                    Prepared[file] = true;
                }
            }

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
    /// Pre-flight check for a user-chosen database path, without initialising or modifying anything. Returns null
    /// when the path is usable: the file doesn't exist yet (it will be created), is empty, is an empty SQLite
    /// database, or is this app's database of the current <see cref="SchemaVersion"/>. Returns the problem for
    /// anything else — a non-SQLite file, some other application's SQLite database, or one of ours from another
    /// version (<see cref="DatabaseProblem.OlderVersion"/> set when it is older and may be set aside).
    ///
    /// Never throws. A database that's merely busy (open in another instance — legal under WAL) must validate
    /// fine, so the header is read with ReadWrite sharing and the probe connection gets a busy timeout; any
    /// genuinely unreadable file comes back as a problem, not a crash.
    /// </summary>
    public static DatabaseProblem? ValidateFile(string path)
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
                    return new DatabaseProblem("The file is not an SQLite database.", null);
                }
            }
            if (!System.Text.Encoding.ASCII.GetString(header, 0, 15).Equals("SQLite format 3", StringComparison.Ordinal))
            {
                return new DatabaseProblem("The file is not an SQLite database.", null);
            }

            // Mode=ReadWrite (no Create) — we only SELECT from sqlite_master and read user_version; nothing is
            // initialised or written by this probe. DefaultTimeout maps to SQLite's busy handler, so a moment of
            // write contention from the other instance waits instead of failing.
            using SqliteConnection conn = new($"Data Source={path};Mode=ReadWrite");
            conn.DefaultTimeout = 5;
            conn.Open();
            return Classify(conn, path, out _);
        }
        catch (Exception ex)
        {
            return new DatabaseProblem($"The file could not be read ({ex.Message}).", null);
        }
    }

    /// <summary>
    /// Null when the open database is usable by this app as it is — empty (<paramref name="isNew"/>), or ours at
    /// <see cref="SchemaVersion"/>; otherwise why not: a foreign database (listing its tables) or ours from another
    /// version.
    /// </summary>
    private static DatabaseProblem? Classify(SqliteConnection conn, string path, out bool isNew)
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

        isNew = tables.Count == 0;
        if (isNew)
        {
            return null; // brand-new/empty database — ours to initialise
        }

        if (!tables.Contains(MarkerTable, StringComparer.OrdinalIgnoreCase))
        {
            string sample = string.Join(", ", tables.Take(5));
            if (tables.Count > 5)
            {
                sample += ", …";
            }
            return new DatabaseProblem(
                $"The file is an SQLite database, but not a QSR Price Benchmarks one (tables found: {sample}).", null);
        }

        int fileVersion;
        using (SqliteCommand cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            fileVersion = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        if (fileVersion == SchemaVersion)
        {
            return null;
        }
        string file = Path.GetFullPath(path);
        return fileVersion < SchemaVersion
            ? new DatabaseProblem(
                $"'{file}' was made by an older version of QSR Price Benchmarks (database version {fileVersion}; " +
                $"this version uses {SchemaVersion}) and cannot be opened by this one. Its runs can only be read by " +
                "the version that made it.", fileVersion)
            : new DatabaseProblem(
                $"'{file}' was made by a newer version of QSR Price Benchmarks (database version {fileVersion}; " +
                $"this version uses {SchemaVersion}). Install the newer version to open it.", null);
    }

    /// <summary>
    /// Moves an older database out of the way, keeping it: renames <paramref name="path"/> to
    /// <c>&lt;name&gt;.v&lt;version&gt;-&lt;yyyyMMdd-HHmmss&gt;&lt;extension&gt;</c> beside it, so the next
    /// <see cref="Open"/> of <paramref name="path"/> starts a fresh database. Only ever called on the user's say-so —
    /// the app never discards a database on its own. Returns the new path.
    /// </summary>
    public static string SetAside(string path, int fileVersion)
    {
        string file = Path.GetFullPath(path);
        Prepared.TryRemove(file, out _); // the fresh file that takes its place must be seeded

        // The -wal beside the file is part of the database: fold it in first, or the renamed file would lose
        // whatever it still held. Pooled connections keep the file open, so they are let go before and after.
        SqliteConnection.ClearAllPools();
        using (SqliteConnection raw = new($"Data Source={file};Mode=ReadWrite;Pooling=False"))
        {
            raw.Open();
            Execute(raw, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        SqliteConnection.ClearAllPools();

        string aside = Path.Combine(Path.GetDirectoryName(file)!,
            $"{Path.GetFileNameWithoutExtension(file)}.v{fileVersion}-" +
            $"{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}{Path.GetExtension(file)}");
        // The window's background readers (runs list, map, each chain's tabs) open the file for an instant to be
        // refused, and a rename landing in that instant fails — measured: about one try in three in the UI tests.
        // So it is retried for a bounded two seconds; a file held open for longer (another window of the older
        // version) still fails, with the reason.
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(file, aside);
                break;
            }
            catch (IOException) when (attempt < 20)
            {
                SqliteConnection.ClearAllPools();
                Thread.Sleep(100);
            }
        }
        foreach (string companion in (string[])[file + "-wal", file + "-shm"])
        {
            if (File.Exists(companion))
            {
                File.Delete(companion); // empty after the checkpoint; it belonged to the file just moved
            }
        }
        return aside;
    }

    [SuppressMessage("Security", "CA2100", Justification = "Runs schema SQL; every caller passes a compile-time constant or a constant-built string.")]
    private static void Execute(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Creates every table of <see cref="SchemaVersion"/> in a new file and stamps the version, in one transaction:
    /// a file can never hold the tables without the stamp. IF NOT EXISTS because two windows may open the same new
    /// file at once; BEGIN IMMEDIATE makes the second wait, and its pass is then a no-op.
    /// </summary>
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

        using SqliteTransaction tx = conn.BeginTransaction();
        ExecuteInTx(conn, tx, ddl);
        ExecuteInTx(conn, tx, $"PRAGMA user_version = {SchemaVersion.ToString(CultureInfo.InvariantCulture)};");
        tx.Commit();
    }

    [SuppressMessage("Security", "CA2100", Justification = "Runs schema SQL; every caller passes a compile-time constant or a constant-built string.")]
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
    [SuppressMessage("Security", "CA2100", Justification = "Interpolates table and column names from the seed's own constants; values are bound as parameters.")]
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

        // One write transaction around the whole DROP + CREATE: the UI opens the database from several tasks at
        // once (choosing a chain rebuilds its tab list and its map together), and two opens interleaving their
        // statements threw "view V_MARKETING_REPORT already exists". BeginTransaction() is BEGIN IMMEDIATE, so a
        // second opener waits on busy_timeout here instead of racing.
        using SqliteTransaction tx = conn.BeginTransaction();
        ExecuteInTx(conn, tx, ddl);
        tx.Commit();
    }
}
