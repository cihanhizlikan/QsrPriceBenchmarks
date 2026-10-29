namespace QsrPriceBenchmarks.Core.Db;

/// <summary>
/// Why a database file cannot be used as it is. <see cref="OlderVersion"/> is set only when the file is this app's
/// own database from an older schema version — the one case where the user may set it aside and start afresh.
/// </summary>
public sealed record DatabaseProblem(string Message, int? OlderVersion);
