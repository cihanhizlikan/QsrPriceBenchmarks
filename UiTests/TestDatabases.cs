using System.IO;
using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;

namespace QsrPriceBenchmarks.UiTests;

/// <summary>
/// A database in a unique temp file for a UI test to point <c>DbPath</c> at,
/// created through the real <see cref="Database.Open"/> (so schema and chains
/// are seeded) and deleted on dispose.
/// </summary>
internal sealed class ThrowawayDatabase : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"qsr_ui_{Guid.NewGuid():N}.sqlite");

    public ThrowawayDatabase(Action<SqliteConnection>? seed = null)
    {
        using SqliteConnection conn = Database.Open(Path);
        seed?.Invoke(conn);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string file in new[] { Path, Path + "-wal", Path + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // best effort: a leftover temp file is harmless
            }
        }
    }
}

/// <summary>
/// The user's own database at the path the view model defaults to. A UI test
/// takes a <see cref="State"/> before and compares after: the main window's
/// view model points at this file until a test repoints it, and touching it
/// would be touching the user's data.
/// </summary>
internal readonly record struct UserDatabaseFile(bool Exists, long Length, DateTime LastWriteUtc)
{
    public static UserDatabaseFile State()
    {
        FileInfo info = new(System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "QsrPriceBenchmarks", Database.DefaultFileName));
        return info.Exists ? new UserDatabaseFile(true, info.Length, info.LastWriteTimeUtc) : default;
    }
}
