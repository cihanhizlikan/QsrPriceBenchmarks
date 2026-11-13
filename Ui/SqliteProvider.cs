using SQLitePCL;

namespace QsrPriceBenchmarks.Ui;

/// <summary>
/// Registers the SQLite that ships with Windows (winsqlite3) as this host's provider. SQLitePCLRaw 3 has no
/// winsqlite3 bundle to do it from a module initializer, so the host does it before the first connection opens
/// (WP-D7). The UI tests call it too, because they open the window without starting the app.
/// </summary>
public static class SqliteProvider
{
    public static void Register()
    {
        raw.SetProvider(new SQLite3Provider_winsqlite3());
    }
}
