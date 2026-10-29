using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Ui;
using QsrPriceBenchmarks.Ui.ViewModels;
using Xunit;

namespace QsrPriceBenchmarks.UiTests;

/// <summary>
/// The app started on an older version's database (plan WP-S5). It never replaces one on its own: it asks once,
/// and only a Yes sets the file aside — renamed, never deleted — so a fresh database takes its place.
/// </summary>
public sealed class OlderDatabaseTests
{
    [Fact]
    public void Answering_Yes_Keeps_The_Old_File_Aside_And_Starts_A_Fresh_Database()
    {
        string path = OlderDatabase();
        List<string> asked = [];
        List<string> log = [];
        try
        {
            StartWindow(path, question => { asked.Add(question); return true; }, log);

            Assert.Single(asked);
            string[] asideFiles = Directory.GetFiles(Path.GetDirectoryName(path)!,
                Path.GetFileNameWithoutExtension(path) + ".v0-*");
            Assert.True(asideFiles.Length == 1, "set aside: " + asideFiles.Length + "; log: " + string.Join(" | ", log));
            string aside = asideFiles[0];
            Assert.Contains(log, l => l.Contains(aside, StringComparison.Ordinal));
            Assert.Null(Database.ValidateFile(path)); // a current database now, usable as it is
        }
        finally
        {
            DeleteAll(path);
        }
    }

    [Fact]
    public void Answering_No_Leaves_The_Old_File_Exactly_As_It_Was()
    {
        string path = OlderDatabase();
        byte[] before = Hash(path);
        List<string> log = [];
        try
        {
            StartWindow(path, _ => false, log);

            Assert.Equal(before, Hash(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!,
                Path.GetFileNameWithoutExtension(path) + ".v0-*"));
            Assert.Contains(log, l => l.Contains("older version", StringComparison.Ordinal));
        }
        finally
        {
            DeleteAll(path);
        }
    }

    private static void StartWindow(string path, Func<string, bool> answer, List<string> log)
    {
        UserDatabaseFile userDatabaseBefore = UserDatabaseFile.State();
        WpfThread.Run(() =>
        {
            MainWindow window = new();
            MainViewModel model = (MainViewModel)window.DataContext;
            model.ConfirmSetAside = answer;
            model.DbPath = path;
            Task startup = model.InitialiseAsync();
            long started = Stopwatch.GetTimestamp();
            while (!startup.IsCompleted)
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(20))
                {
                    throw new TimeoutException("InitialiseAsync did not finish within 20 s.");
                }
                DispatcherFrame frame = new();
                DispatcherTimer tick = new(TimeSpan.FromMilliseconds(25), DispatcherPriority.Background,
                    (_, _) => { frame.Continue = false; }, Dispatcher.CurrentDispatcher);
                tick.Start();
                Dispatcher.PushFrame(frame);
                tick.Stop();
            }
            log.AddRange(model.SelectedSession!.LogLines);
            window.Close();
        });
        SqliteConnection.ClearAllPools();
        Assert.Equal(userDatabaseBefore, UserDatabaseFile.State());
    }

    /// <summary>One of ours with a finished run, stamped back to version 0 — every file from before the stamp.</summary>
    private static string OlderDatabase()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"qsr_ui_older_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Database.DefaultFileName);
        using (SqliteConnection conn = Database.Open(path))
        {
            long qsr = Repository.QsrId(conn, ChainRegistry.All[0].Name);
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

    private static void DeleteAll(string path)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
        catch (IOException)
        {
            // best effort: a leftover temp folder is harmless
        }
    }
}
