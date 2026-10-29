using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// Since 2026-10-29 tables are never migrated: a new database is stamped with <see cref="Database.SchemaVersion"/>,
/// a file of any other version is refused untouched, and an older one is set aside — kept, never deleted — only on
/// the user's say-so (plan WP-S5). The trap this guards: without the stamp, an older file passed the "is it ours"
/// check and then failed mid-use on a column it did not have.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Db")]
public sealed class SchemaVersionTests
{
    [Fact(DisplayName = "A new database is stamped with the current schema version")]
    public void NewDatabase_IsStamped()
    {
        using TempDb db = new();

        Assert.Equal((long)Database.SchemaVersion, Convert.ToInt64(db.Scalar("PRAGMA user_version;")));
        Assert.Null(Database.ValidateFile(db.Path));
    }

    [Fact(DisplayName = "An older version's database is refused, named, and left byte for byte as it was")]
    public void OlderDatabase_IsRefusedUntouched()
    {
        string path = OlderDatabaseWithARun();
        try
        {
            byte[] before = Hash(path);

            OutdatedDatabaseException refused = Assert.Throws<OutdatedDatabaseException>(() => Database.Open(path));
            SqliteConnection.ClearAllPools();

            Assert.Equal(0, refused.FileVersion);
            Assert.Equal(Path.GetFullPath(path), refused.Path);
            Assert.Contains(Path.GetFullPath(path), refused.Message, StringComparison.Ordinal);
            Assert.Equal(before, Hash(path));
            Assert.Equal(0, Database.ValidateFile(path)?.OlderVersion);
        }
        finally
        {
            Delete(path);
        }
    }

    [Fact(DisplayName = "A newer version's database is refused and not offered for setting aside")]
    public void NewerDatabase_IsRefusedWithoutAnOffer()
    {
        using TempDb db = new();
        db.Scalar($"PRAGMA user_version = {Database.SchemaVersion + 1};");
        SqliteConnection.ClearAllPools();

        DatabaseProblem? problem = Database.ValidateFile(db.Path);

        Assert.NotNull(problem);
        Assert.Null(problem.OlderVersion);
        Assert.Contains("newer version", problem.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Setting an older database aside keeps it, renamed with its data, and frees the path")]
    public void SetAside_KeepsTheOldFile_AndFreesThePath()
    {
        string path = OlderDatabaseWithARun();
        string? aside = null;
        try
        {
            aside = Database.SetAside(path, fileVersion: 0);

            Assert.False(File.Exists(path));
            Assert.StartsWith(Path.GetFileNameWithoutExtension(path) + ".v0-", Path.GetFileName(aside),
                StringComparison.Ordinal);
            using (SqliteConnection kept = new($"Data Source={aside};Mode=ReadOnly;Pooling=False"))
            {
                kept.Open();
                using SqliteCommand count = kept.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM SCRAPE_RUNS;";
                Assert.Equal(1L, Convert.ToInt64(count.ExecuteScalar()));
            }

            using SqliteConnection fresh = Database.Open(path);
            using SqliteCommand runs = fresh.CreateCommand();
            runs.CommandText = "SELECT COUNT(*) FROM SCRAPE_RUNS;";
            Assert.Equal(0L, Convert.ToInt64(runs.ExecuteScalar()));
        }
        finally
        {
            Delete(path);
            if (aside is not null)
            {
                Delete(aside);
            }
        }
    }

    /// <summary>
    /// A database of ours with one finished run, then stamped back to version 0 — what every file made before
    /// the stamp existed looks like.
    /// </summary>
    private static string OlderDatabaseWithARun()
    {
        string path = Path.Combine(Path.GetTempPath(), $"qsr_older_{Guid.NewGuid():N}.sqlite");
        using (SqliteConnection conn = Database.Open(path))
        {
            long qsr = Repository.QsrId(conn, Core.Models.ChainRegistry.All[0].Name);
            using RunLock run = Repository.StartScrapeRun(conn, qsr);
            Repository.FinishScrapeRun(conn, run.RunId);
            using SqliteCommand stamp = conn.CreateCommand();
            stamp.CommandText = "PRAGMA user_version = 0; PRAGMA wal_checkpoint(TRUNCATE);";
            stamp.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        return path;
    }

    private static byte[] Hash(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return SHA256.HashData(stream);
    }

    private static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (string file in (string[])[path, path + "-wal", path + "-shm"])
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
