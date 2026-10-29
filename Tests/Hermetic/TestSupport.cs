using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Util;
using SQLitePCL;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// Registers the SQLitePCLRaw native provider exactly once when this test
/// assembly loads. The test project references the cross-platform
/// <c>bundle_e_sqlite3</c> (the shipping apps use bundle_winsqlite3); calling
/// <see cref="Batteries_V2.Init"/> here is idempotent and guarantees the
/// provider is set before any test opens a connection, regardless of test
/// runner module-initializer behaviour.
/// </summary>
internal static class SqliteInit
{
    [ModuleInitializer]
    internal static void Init() => Batteries_V2.Init();
}

/// <summary>
/// A throwaway, fully-migrated SQLite database in a unique temp file, opened
/// through the real <see cref="Database.Open"/> path so tests exercise the
/// production schema, seed data, migrations, and views. Disposing closes the
/// connection, clears the pool (releasing the file handle on Windows), and
/// deletes the file.
/// </summary>
internal sealed class TempDb : IDisposable
{
    public string Path { get; }
    public SqliteConnection Conn { get; }

    public TempDb()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"qsr_test_{Guid.NewGuid():N}.sqlite");
        Conn = Database.Open(Path);
    }

    /// <summary>Run a scalar query against the test connection.</summary>
    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using SqliteCommand cmd = Conn.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string n, object? v) in args)
        {
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        }
        return cmd.ExecuteScalar();
    }

    public void Dispose()
    {
        Conn.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
        catch { /* best-effort cleanup */ }
    }
}

/// <summary>
/// Seeding helpers for tests that need a row the production code only ever
/// writes in bulk. <see cref="InsertPlatformLocation"/> lived in
/// <c>Repository</c> as <c>UpsertPlatformLocation</c> with no production
/// caller; it moved here (plan WP-D2) so the repository holds only what the
/// app uses.
/// </summary>
internal static class TestData
{
    /// <summary>
    /// Moves the closed database at <paramref name="path"/> to a new path and returns it: the file as a restarted
    /// app first opens it. <see cref="Database.Open"/> seeds and rebuilds the views only on a file's first open in
    /// a process, so a test meaning "re-open" that reuses the path skips exactly what it means to exercise
    /// (measured 2026-10-29: such tests stayed green with a seed that re-enabled disabled tabs).
    /// </summary>
    public static string MovedAsIfRestarted(string path)
    {
        SqliteConnection.ClearAllPools(); // the last connection closing folds the WAL into the file
        string moved = Path.Combine(Path.GetDirectoryName(path)!, $"qsr_restarted_{Guid.NewGuid():N}.sqlite");
        File.Move(path, moved);
        return moved;
    }

    /// <summary>
    /// Inserts one active PLATFORM_LOCATIONS row and returns its ID. The name
    /// is title-cased the way the production census writes names.
    /// </summary>
    public static long InsertPlatformLocation(
        SqliteConnection conn, long qsrId, long platformId, string slug, string? name = null)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO PLATFORM_LOCATIONS (QSR_ID, PLATFORM_ID, SLUG, NAME)
            VALUES ($qsr, $plat, $slug, $name);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$qsr", qsrId);
        cmd.Parameters.AddWithValue("$plat", platformId);
        cmd.Parameters.AddWithValue("$slug", slug);
        cmd.Parameters.AddWithValue("$name", (object?)TextNormalization.Title(name) ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
