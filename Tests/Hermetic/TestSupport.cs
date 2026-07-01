using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
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
        using var cmd = Conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args)
            cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteScalar();
    }

    public void Dispose()
    {
        Conn.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(Path)) File.Delete(Path); }
        catch { /* best-effort cleanup */ }
    }
}
