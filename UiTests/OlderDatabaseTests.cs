using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
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

    /// <summary>
    /// After a No, the window went on opening the refused file from every reader — each chain's tab list, the map,
    /// the runs list — on every refresh, each refusal caught again (maintainer, 2026-10-29: the debugger stopped on
    /// every one). A refused file must not be opened at all until the user changes something.
    /// </summary>
    [Fact]
    public void After_No_Nothing_Opens_The_Refused_File_Again()
    {
        string path = OlderDatabase();
        List<string> thrown = [];
        int runsShown = -1;
        try
        {
            WpfThread.Run(() =>
            {
                MainWindow window = new();
                MainViewModel model = (MainViewModel)window.DataContext;
                model.ShowMessage = (_, _, _, _) => MessageBoxResult.No;
                EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> onThrow = (_, e) =>
                {
                    if (e.Exception is SqliteException or OutdatedDatabaseException)
                    {
                        lock (thrown)
                        {
                            thrown.Add(e.Exception.Message);
                        }
                    }
                };
                AppDomain.CurrentDomain.FirstChanceException += onThrow;
                try
                {
                    model.DbPath = path;
                    Task startup = model.InitialiseAsync();
                    Pump(() => startup.IsCompleted);
                    foreach (ChainViewModel chain in model.Chains)
                    {
                        model.SelectedChain = chain; // each chain's tab list and the map re-read
                    }
                    model.ReloadRunsCommand.Execute(null);
                    Pump(() => false, TimeSpan.FromSeconds(1));
                    runsShown = model.Runs.Count;
                }
                finally
                {
                    AppDomain.CurrentDomain.FirstChanceException -= onThrow;
                    window.Close();
                }
            });

            Assert.True(thrown.Count == 0, $"{thrown.Count} database exception(s): {string.Join(" | ", thrown.Distinct())}");
            Assert.Equal(0, runsShown);
        }
        finally
        {
            DeleteAll(path);
        }
    }

    /// <summary>
    /// "Delete all before" had no catch: on a refused or locked database its failure reached the app's last-resort
    /// handler. It now reports the failure like the single delete does.
    /// </summary>
    [Fact]
    public void Delete_All_Before_On_A_Refused_Database_Reports_The_Failure()
    {
        string path = OlderDatabase();
        List<string> shown = [];
        try
        {
            WpfThread.Run(() =>
            {
                MainWindow window = new();
                MainViewModel model = (MainViewModel)window.DataContext;
                model.ShowMessage = (text, caption, _, _) =>
                {
                    shown.Add($"{caption}: {text}");
                    return caption == "Older database" ? MessageBoxResult.No : MessageBoxResult.Yes;
                };
                model.DbPath = path;
                Task startup = model.InitialiseAsync();
                Pump(() => startup.IsCompleted);

                model.DeleteBeforeId = "5";
                model.DeleteBeforeIdCommand.Execute(null);
                Pump(() => shown.Any(s => s.StartsWith("Delete:", StringComparison.Ordinal)));
                window.Close();
            });

            Assert.Contains(shown, s => s.StartsWith("Delete: Delete failed", StringComparison.Ordinal));
        }
        finally
        {
            DeleteAll(path);
        }
    }

    /// <summary>Runs the dispatcher until <paramref name="done"/> holds, or for <paramref name="limit"/> at most.</summary>
    private static void Pump(Func<bool> done, TimeSpan? limit = null)
    {
        TimeSpan bound = limit ?? TimeSpan.FromSeconds(20);
        long started = Stopwatch.GetTimestamp();
        while (!done())
        {
            if (Stopwatch.GetElapsedTime(started) > bound)
            {
                if (limit is not null)
                {
                    return; // a fixed settling time, not a wait for something
                }
                throw new TimeoutException("The window did not reach the expected state within 20 s.");
            }
            DispatcherFrame frame = new();
            DispatcherTimer tick = new(TimeSpan.FromMilliseconds(25), DispatcherPriority.Background,
                (_, _) => { frame.Continue = false; }, Dispatcher.CurrentDispatcher);
            tick.Start();
            Dispatcher.PushFrame(frame);
            tick.Stop();
        }
    }

    private static void StartWindow(string path, Func<string, bool> answer, List<string> log)
    {
        UserDatabaseFile userDatabaseBefore = UserDatabaseFile.State();
        WpfThread.Run(() =>
        {
            MainWindow window = new();
            MainViewModel model = (MainViewModel)window.DataContext;
            model.ShowMessage = (question, _, _, _) => answer(question) ? MessageBoxResult.Yes : MessageBoxResult.No;
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
