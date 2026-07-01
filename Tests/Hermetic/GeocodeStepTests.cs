using QsrPriceBenchmarks.Core.Db;
using QsrPriceBenchmarks.Core.Scraping;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// Regression test for the Step 3 failure: the chain-filtered geocode queries
/// concatenated raw-string fragments into "... = $qsrORDER BY ID", which
/// SQLite read as the parameter "$qsrORDER" and rejected with
/// 'near "BY": syntax error'. With no rows needing coordinates, no browser
/// launches, so this runs hermetically and simply asserts the SQL is valid.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "Db")]
public sealed class GeocodeStepTests
{
    [Fact(DisplayName = "Chain-filtered geocode runs without an SQL syntax error")]
    public async Task RunAsync_WithChainFilter_NoSqlError()
    {
        using var db = new TempDb();
        var qsr = Repository.QsrId(db.Conn, "Burger King");

        // Must not throw 'near "BY": syntax error'.
        await GeocodeAndMatchStep.RunAsync(db.Conn, qsr, rematch: false);
    }

    [Fact(DisplayName = "Whole-DB geocode with rematch also runs cleanly")]
    public async Task RunAsync_NoFilter_Rematch_Ok()
    {
        using var db = new TempDb();
        await GeocodeAndMatchStep.RunAsync(db.Conn, chainQsrId: null, rematch: true);
    }
}
