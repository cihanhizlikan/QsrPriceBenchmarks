namespace QsrPriceBenchmarks.Core.Db;

/// <summary>
/// <see cref="Database.Open"/> refused a database made by an older version of this app. Tables are never migrated
/// (the database is disposable since 2026-10-29); a host offers <see cref="Database.SetAside"/> instead — on the
/// user's say-so, never on its own.
/// </summary>
public sealed class OutdatedDatabaseException(string message, string path, int fileVersion)
    : InvalidOperationException(message)
{
    public string Path { get; } = path;

    public int FileVersion { get; } = fileVersion;
}
