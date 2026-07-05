using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// Databases created before v1.1 modelled Turkish İl as CITIES and İlçe as
/// PROVINCES (with LOCATIONS.PROVINCE_ID pointing at the İlçe table).
/// Opening such a file must migrate it in place — tables renamed, data and
/// foreign keys intact — so the map and reports work instead of failing
/// silently on the missing DISTRICT_ID column.
/// </summary>
public class DatabaseMigrationTests
{
    [Fact]
    public void Open_LegacyCitySchema_MigratesTablesAndKeepsData()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qsr-legacy-{Guid.NewGuid():N}.sqlite");
        try
        {
            CreateLegacyDatabase(path);

            using (SqliteConnection conn = Database.Open(path))
            {
                // Old names are gone; new names exist.
                Assert.False(TableExists(conn, "CITIES"));
                Assert.True(TableExists(conn, "PROVINCES"));
                Assert.True(TableExists(conn, "DISTRICTS"));

                // Data survived the renames with FK links intact, and the
                // renamed columns answer the exact query the map runs.
                long qsrId = Repository.QsrId(conn, "Burger King");
                List<ProvinceDistrictCount> counts =
                    Repository.GetLocationCountsByProvince(conn, qsrId);

                ProvinceDistrictCount row = Assert.Single(counts);
                Assert.Equal("Istanbul", row.Province);
                Assert.Equal("Kadikoy", row.District);
                Assert.Equal(1, row.Count);
            }

            // A second open of the migrated file must be a no-op, not a crash.
            using (SqliteConnection conn = Database.Open(path))
            {
                Assert.True(TableExists(conn, "PROVINCES"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(path);
        }
    }

    [Fact]
    public void Open_HalfOpenedLegacyFile_StillMigrates()
    {
        // Simulates a file that was already opened once by post-rename code
        // WITHOUT the migration: ApplySchema left behind an empty new-schema
        // DISTRICTS table next to the legacy tables.
        string path = Path.Combine(Path.GetTempPath(), $"qsr-legacy-half-{Guid.NewGuid():N}.sqlite");
        try
        {
            CreateLegacyDatabase(path);
            using (SqliteConnection raw = new($"Data Source={path}"))
            {
                raw.Open();
                Execute(raw, """
                    CREATE TABLE DISTRICTS (
                        ID          INTEGER PRIMARY KEY AUTOINCREMENT,
                        PROVINCE_ID INTEGER NOT NULL,
                        NAME        TEXT    NOT NULL
                    );
                    """);
            }
            SqliteConnection.ClearAllPools();

            using SqliteConnection conn = Database.Open(path);
            long qsrId = Repository.QsrId(conn, "Burger King");
            ProvinceDistrictCount row =
                Assert.Single(Repository.GetLocationCountsByProvince(conn, qsrId));
            Assert.Equal("Istanbul", row.Province);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(path);
        }
    }

    /// <summary>
    /// Builds a minimal pre-v1.1 database by hand: legacy CITIES / PROVINCES
    /// (İlçe) tables, LOCATIONS with a PROVINCE_ID column, one seeded Burger
    /// King location, plus an old-style view so the migration's view-drop
    /// path is exercised too.
    /// </summary>
    private static void CreateLegacyDatabase(string path)
    {
        using SqliteConnection conn = new($"Data Source={path}");
        conn.Open();
        Execute(conn, """
            CREATE TABLE QSR (
                ID       INTEGER PRIMARY KEY AUTOINCREMENT,
                NAME     TEXT    NOT NULL UNIQUE,
                BASE_URL TEXT    NOT NULL
            );

            CREATE TABLE CITIES (
                ID   INTEGER PRIMARY KEY AUTOINCREMENT,
                NAME TEXT NOT NULL UNIQUE
            );

            CREATE TABLE PROVINCES (
                ID      INTEGER PRIMARY KEY AUTOINCREMENT,
                CITY_ID INTEGER NOT NULL REFERENCES CITIES(ID),
                NAME    TEXT    NOT NULL,
                UNIQUE (CITY_ID, NAME)
            );

            CREATE TABLE LOCATIONS (
                ID           INTEGER PRIMARY KEY AUTOINCREMENT,
                QSR_ID       INTEGER NOT NULL REFERENCES QSR(ID),
                PROVINCE_ID  INTEGER REFERENCES PROVINCES(ID),
                SLUG         TEXT    NOT NULL,
                NAME         TEXT,
                ADDRESS      TEXT,
                LATITUDE     REAL,
                LONGITUDE    REAL,
                ACTIVE       INTEGER NOT NULL DEFAULT 1,
                LAST_SEEN_AT TEXT,
                UNIQUE (QSR_ID, SLUG)
            );

            INSERT INTO QSR (NAME, BASE_URL) VALUES ('Burger King', 'https://example.test/');
            INSERT INTO CITIES (NAME) VALUES ('Istanbul');
            INSERT INTO PROVINCES (CITY_ID, NAME) VALUES (1, 'Kadikoy');
            INSERT INTO LOCATIONS (QSR_ID, PROVINCE_ID, SLUG) VALUES (1, 1, 'bk-kadikoy');

            CREATE VIEW V_MARKETING_REPORT AS
                SELECT c.NAME AS CITY, p.NAME AS PROVINCE
                FROM LOCATIONS l
                JOIN PROVINCES p ON p.ID = l.PROVINCE_ID
                JOIN CITIES    c ON c.ID = p.CITY_ID;
            """);
        SqliteConnection.ClearAllPools();
    }

    /// <summary>Delete the database plus any WAL/SHM sidecars Database.Open created.</summary>
    private static void DeleteDatabaseFiles(string path)
    {
        foreach (string file in new[] { path, path + "-wal", path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static void Execute(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection conn, string table)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        cmd.Parameters.AddWithValue("$name", table);
        return cmd.ExecuteScalar() is not null;
    }
}
