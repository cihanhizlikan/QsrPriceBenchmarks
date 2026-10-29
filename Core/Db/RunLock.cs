using Microsoft.Data.Sqlite;

namespace QsrPriceBenchmarks.Core.Db;

/// <summary>
/// The claim a scrape holds on its run while it works on it: an open, unshared handle on
/// <c>&lt;database&gt;.run-&lt;id&gt;.lock</c> beside the database. Windows closes the handle when the process
/// ends however it ends — exit, crash, Task Manager, power loss and reboot — so no crash can leave a run claimed.
/// The claim is the open handle, not the file: a file left behind locks nothing, and the next probe removes it.
/// Without it a run alive in another window looked exactly like a crashed one, and could be resumed twice.
/// </summary>
public sealed class RunLock : IDisposable
{
    private readonly FileStream? _handle;

    private RunLock(long runId, FileStream? handle)
    {
        RunId = runId;
        _handle = handle;
    }

    public long RunId { get; }

    /// <summary>
    /// Claims <paramref name="runId"/>, or returns null when another holder has it. Retried briefly, because a
    /// window probing the run (<see cref="IsHeld"/>) holds the file for an instant.
    /// </summary>
    public static RunLock? TryAcquire(SqliteConnection conn, long runId)
    {
        string? path = PathFor(conn, runId);
        if (path is null)
        {
            return new RunLock(runId, null); // an in-memory database: no other process can reach it
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return new RunLock(runId, Open(path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < 3)
            {
                Thread.Sleep(100);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>True while some process — this one included — holds the claim on <paramref name="runId"/>.</summary>
    public static bool IsHeld(SqliteConnection conn, long runId)
    {
        string? path = PathFor(conn, runId);
        if (path is null || !File.Exists(path))
        {
            return false;
        }

        try
        {
            // Opening it means nobody holds it: a leftover from a crash, deleted as this handle closes.
            using FileStream probe = Open(path);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false; // already being deleted by a holder that just let go
        }
    }

    public void Dispose()
    {
        _handle?.Dispose();
    }

    private static FileStream Open(string path)
    {
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1,
            FileOptions.DeleteOnClose);
    }

    private static string? PathFor(SqliteConnection conn, long runId)
    {
        string file = conn.DataSource;
        return string.IsNullOrEmpty(file) || file.Contains(":memory:", StringComparison.Ordinal)
            ? null
            : $"{file}.run-{runId}.lock";
    }
}
