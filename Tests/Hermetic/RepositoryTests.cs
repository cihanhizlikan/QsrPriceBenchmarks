using QsrPriceBenchmarks.Core.Db;
using Microsoft.Data.Sqlite;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

[Trait("Category", "Unit")]
[Trait("Category", "Db")]
public sealed class RepositoryTests
{
    private const string Chain = "Burger King";

    private static (long Qsr, long Platform) Ids(TempDb db) =>
        (Repository.QsrId(db.Conn, Chain), Repository.PlatformId(db.Conn, "Tıkla Gelsin"));

    private (string? Name, string? Address, double? Lat, double? Lon) ReadLocation(TempDb db, string slug, long qsr)
    {
        using SqliteCommand cmd = db.Conn.CreateCommand();
        cmd.CommandText = "SELECT NAME, ADDRESS, LATITUDE, LONGITUDE FROM LOCATIONS WHERE SLUG=$s AND QSR_ID=$q;";
        cmd.Parameters.AddWithValue("$s", slug);
        cmd.Parameters.AddWithValue("$q", qsr);
        using SqliteDataReader r = cmd.ExecuteReader();
        Assert.True(r.Read());
        return (
            r.IsDBNull(0) ? null : r.GetString(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetDouble(2),
            r.IsDBNull(3) ? null : r.GetDouble(3));
    }

    [Fact(DisplayName = "Lookups: unknown QSR / platform throw")]
    public void Lookups_Unknown_Throw()
    {
        using TempDb db = new();
        Assert.Throws<InvalidOperationException>(() => Repository.QsrId(db.Conn, "Nonexistent"));
        Assert.Throws<InvalidOperationException>(() => Repository.PlatformId(db.Conn, "Nonexistent"));
    }

    [Fact(DisplayName = "UpsertLocation is idempotent and skips 'test' slugs")]
    public void UpsertLocation_IdempotentAndTestSkip()
    {
        using TempDb db = new();
        (long qsr, long _) = Ids(db);

        long? id1 = Repository.UpsertLocation(db.Conn, "Istanbul", "Kadikoy", "kadikoy-1", qsr);
        long? id2 = Repository.UpsertLocation(db.Conn, "Istanbul", "Kadikoy", "kadikoy-1", qsr);

        Assert.NotNull(id1);
        Assert.Equal(id1, id2);
        Assert.Null(Repository.UpsertLocation(db.Conn, "Istanbul", "Kadikoy", "test-slug", qsr));
    }

    [Fact(DisplayName = "UpdateLocationDetails nulls coords only when the ADDRESS changes")]
    public void UpdateLocationDetails_CoordInvalidation()
    {
        using TempDb db = new();
        (long qsr, long _) = Ids(db);
        Repository.UpsertLocation(db.Conn, "Istanbul", "Kadikoy", "k1", qsr);

        Repository.UpdateLocationDetails(db.Conn, "k1", "BK Kadikoy", "Caferaga Mah No 12", qsr);
        Repository.UpdateLocationCoords(db.Conn, "k1", 40.0, 29.0, qsr);

        // Same details again → coords preserved.
        Repository.UpdateLocationDetails(db.Conn, "k1", "BK Kadikoy", "Caferaga Mah No 12", qsr);
        (string? Name, string? Address, double? Lat, double? Lon) afterSame = ReadLocation(db, "k1", qsr);
        Assert.Equal(40.0, afterSame.Lat);
        Assert.Equal(29.0, afterSame.Lon);

        // Changed NAME only (address unchanged) → coords PRESERVED. The geocoder
        // keys on the address, so a name-only edit must not force a re-geocode.
        Repository.UpdateLocationDetails(db.Conn, "k1", "BK Kadikoy Merkez", "Caferaga Mah No 12", qsr);
        (string? Name, string? Address, double? Lat, double? Lon) afterNameChange = ReadLocation(db, "k1", qsr);
        Assert.Equal(40.0, afterNameChange.Lat);
        Assert.Equal(29.0, afterNameChange.Lon);

        // Changed ADDRESS → coords invalidated so Step 3 re-geocodes.
        Repository.UpdateLocationDetails(db.Conn, "k1", "BK Kadikoy Merkez", "Moda Cad No 99", qsr);
        (string? Name, string? Address, double? Lat, double? Lon) afterAddrChange = ReadLocation(db, "k1", qsr);
        Assert.Null(afterAddrChange.Lat);
        Assert.Null(afterAddrChange.Lon);
    }

    [Fact(DisplayName = "UpdateLocationDetails rejects slogan-looking addresses")]
    public void UpdateLocationDetails_RejectsSlogan()
    {
        using TempDb db = new();
        (long qsr, long _) = Ids(db);
        Repository.UpsertLocation(db.Conn, "Istanbul", "Kadikoy", "k2", qsr);

        Repository.UpdateLocationDetails(db.Conn, "k2", "Lezzet Durağı", "Her An Yanında!", qsr);

        (string? Name, string? Address, double? Lat, double? Lon) row = ReadLocation(db, "k2", qsr);
        Assert.Equal("Lezzet Durağı", row.Name);
        Assert.Null(row.Address); // slogan dropped, not stored
    }

    [Fact(DisplayName = "Census inserts new, skips test slugs, reactivates returners, delists the missing")]
    public void BulkUpsert_CensusSemantics()
    {
        using TempDb db = new();
        (long qsr, long plat) = Ids(db);

        Repository.PlatformCensusResult first = Repository.UpsertPlatformLocationsBulk(
            db.Conn, qsr, plat, ["a", "b", "test-x", "c"]);
        Assert.Equal(3, first.Inserted);
        Assert.Equal(0, first.Reactivated);
        Assert.Empty(first.Delisted);

        // Next census: "c" is gone (delisted), "d" is new. One transaction,
        // full reconciliation — "c" survives as a soft-deleted row.
        Repository.PlatformCensusResult second = Repository.UpsertPlatformLocationsBulk(
            db.Conn, qsr, plat, ["a", "b", "d"]);
        Assert.Equal(1, second.Inserted);
        Assert.Equal(new[] { "c" }, second.Delisted.ToArray());
        Assert.Equal(4L, Convert.ToInt64(db.Scalar(
            "SELECT COUNT(*) FROM PLATFORM_LOCATIONS WHERE QSR_ID=$q AND PLATFORM_ID=$p;",
            ("$q", qsr), ("$p", plat))));
        Assert.Equal(3L, Convert.ToInt64(db.Scalar(
            "SELECT COUNT(*) FROM PLATFORM_LOCATIONS WHERE QSR_ID=$q AND PLATFORM_ID=$p AND ACTIVE=1;",
            ("$q", qsr), ("$p", plat))));

        // "c" comes back: reactivated in place (same row, history kept).
        Repository.PlatformCensusResult third = Repository.UpsertPlatformLocationsBulk(
            db.Conn, qsr, plat, ["a", "b", "c", "d"]);
        Assert.Equal(0, third.Inserted);
        Assert.Equal(1, third.Reactivated);
        Assert.Empty(third.Delisted);
        Assert.Equal(4L, Convert.ToInt64(db.Scalar(
            "SELECT COUNT(*) FROM PLATFORM_LOCATIONS WHERE QSR_ID=$q AND PLATFORM_ID=$p AND ACTIVE=1;",
            ("$q", qsr), ("$p", plat))));

        // An EMPTY census is a fetch anomaly, never a mass delisting.
        Repository.PlatformCensusResult empty = Repository.UpsertPlatformLocationsBulk(
            db.Conn, qsr, plat, ["test-y"]);
        Assert.Empty(empty.Delisted);
        Assert.Equal(4L, Convert.ToInt64(db.Scalar(
            "SELECT COUNT(*) FROM PLATFORM_LOCATIONS WHERE QSR_ID=$q AND PLATFORM_ID=$p AND ACTIVE=1;",
            ("$q", qsr), ("$p", plat))));
    }

    [Fact(DisplayName = "UpdatePlatformLocation nulls coords when ADDRESS changes")]
    public void UpdatePlatformLocation_AddressChange_NullsCoords()
    {
        using TempDb db = new();
        (long qsr, long plat) = Ids(db);
        long plId = TestData.InsertPlatformLocation(db.Conn, qsr, plat, "pl1");

        Repository.UpdatePlatformLocation(db.Conn, plId, address: "Eski Adres No 1");
        Repository.UpdatePlatformLocation(db.Conn, plId, lat: 40.0, lon: 29.0);
        Repository.UpdatePlatformLocation(db.Conn, plId, address: "Yeni Adres No 2");

        using SqliteCommand cmd = db.Conn.CreateCommand();
        cmd.CommandText = "SELECT LATITUDE, LONGITUDE FROM PLATFORM_LOCATIONS WHERE ID=$id;";
        cmd.Parameters.AddWithValue("$id", plId);
        using SqliteDataReader r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.True(r.IsDBNull(0));
        Assert.True(r.IsDBNull(1));
    }

    [Fact(DisplayName = "GetOrCreateMenuItem is idempotent per (qsr, name)")]
    public void GetOrCreateMenuItem_Idempotent()
    {
        using TempDb db = new();
        (long qsr, long _) = Ids(db);
        long a = Repository.GetOrCreateMenuItem(db.Conn, qsr, "Whopper");
        long b = Repository.GetOrCreateMenuItem(db.Conn, qsr, "Whopper");
        Assert.Equal(a, b);
    }

    [Fact(DisplayName = "Scrape run lifecycle: start, snapshot, finish, list, delete")]
    public void ScrapeRun_Lifecycle()
    {
        using TempDb db = new();
        (long qsr, long plat) = Ids(db);
        long plId = TestData.InsertPlatformLocation(db.Conn, qsr, plat, "pl1");
        long item = Repository.GetOrCreateMenuItem(db.Conn, qsr, "Whopper");

        long run;
        using (RunLock claim = Repository.StartScrapeRun(db.Conn, qsr))
        {
            run = claim.RunId;
            Repository.InsertSnapshot(db.Conn, run, plId, item, 100m);
            Repository.FinishScrapeRun(db.Conn, run);
        }

        Assert.Contains(Repository.ListScrapeRuns(db.Conn), x => x.Id == run && x.FinishedAt is not null);

        (bool found, long qsrId, _, int count) = Repository.GetScrapeRunForDeletion(db.Conn, run);
        Assert.True(found);
        Assert.Equal(qsr, qsrId);
        Assert.Equal(1, count);

        Repository.DeleteScrapeRun(db.Conn, run);
        Assert.False(Repository.GetScrapeRunForDeletion(db.Conn, run).Found);
        Assert.Equal(0L, Convert.ToInt64(db.Scalar(
            "SELECT COUNT(*) FROM MENU_SNAPSHOTS WHERE SCRAPE_RUN_ID=$r;", ("$r", run))));
    }

    [Fact(DisplayName = "DeleteScrapeRunsBefore removes only earlier runs")]
    public void DeleteScrapeRunsBefore()
    {
        using TempDb db = new();
        (long qsr, long _) = Ids(db);
        long r1 = CrashedRun(db, qsr);
        long r2 = CrashedRun(db, qsr);
        long r3 = CrashedRun(db, qsr);

        int affected = Repository.DeleteScrapeRunsBefore(db.Conn, r3);

        Assert.Equal(2, affected); // r1 + r2
        List<long> remaining = [.. Repository.ListScrapeRuns(db.Conn).Select(x => x.Id)];
        Assert.DoesNotContain(r1, remaining);
        Assert.DoesNotContain(r2, remaining);
        Assert.Contains(r3, remaining);
    }

    [Fact(DisplayName = "A run being scraped is never deleted, singly or by DeleteScrapeRunsBefore")]
    public void RunBeingScraped_IsNotDeleted()
    {
        using TempDb db = new();
        (long qsr, long _) = Ids(db);
        long crashed = CrashedRun(db, qsr);
        using RunLock live = Repository.StartScrapeRun(db.Conn, qsr);
        long later = CrashedRun(db, qsr);

        Assert.Throws<InvalidOperationException>(() => Repository.DeleteScrapeRun(db.Conn, live.RunId));
        int affected = Repository.DeleteScrapeRunsBefore(db.Conn, later);

        Assert.Equal(1, affected);
        List<long> remaining = [.. Repository.ListScrapeRuns(db.Conn).Select(x => x.Id)];
        Assert.DoesNotContain(crashed, remaining);
        Assert.Contains(live.RunId, remaining);
    }

    /// <summary>A run left unfinished and unclaimed — what a scrape killed mid-run leaves behind.</summary>
    private static long CrashedRun(TempDb db, long qsr)
    {
        using RunLock run = Repository.StartScrapeRun(db.Conn, qsr);
        return run.RunId;
    }

    [Fact(DisplayName = "DeactivateStaleLocations soft-closes unseen rows; Upsert reactivates them")]
    public void SoftDelete_StaleClosedThenReactivated()
    {
        using TempDb db = new();
        (long qsr, long _) = Ids(db);

        long k1 = Repository.UpsertLocation(db.Conn, "Istanbul", "Kadikoy", "k1", qsr)!.Value;
        long k2 = Repository.UpsertLocation(db.Conn, "Istanbul", "Kadikoy", "k2", qsr)!.Value;

        // k1 was seen on Jan 2; k2 only on a prior crawl (Jan 1).
        SetLastSeen(db, k1, "2026-01-02T00:00:00Z");
        SetLastSeen(db, k2, "2026-01-01T00:00:00Z");

        // A crawl beginning Jan 2 re-saw k1 (>= start) but not k2 (< start).
        int closed = Repository.DeactivateStaleLocations(db.Conn, qsr, "2026-01-02T00:00:00Z");

        Assert.Equal(1, closed);
        Assert.True(IsActive(db, k1));
        Assert.False(IsActive(db, k2));
        Assert.Equal(2, Repository.CountLocations(db.Conn, qsr));                  // total preserved
        Assert.Equal(1, Repository.CountLocations(db.Conn, qsr, activeOnly: true)); // only k1 active

        // Re-running the same crawl window is a no-op: k1 isn't stale at the Jan 2
        // boundary, and k2 is already closed (excluded by the ACTIVE = 1 guard).
        Assert.Equal(0, Repository.DeactivateStaleLocations(db.Conn, qsr, "2026-01-02T00:00:00Z"));

        // The slug reappears in a later crawl → the SAME row reactivates,
        // keeping its ID (and therefore its history/match) intact.
        long k2Again = Repository.UpsertLocation(db.Conn, "Istanbul", "Kadikoy", "k2", qsr)!.Value;
        Assert.Equal(k2, k2Again);
        Assert.True(IsActive(db, k2));
        Assert.Equal(2, Repository.CountLocations(db.Conn, qsr, activeOnly: true));
    }

    [Fact(DisplayName = "Reconcile only closes locations in districts visited this pass")]
    public void SoftDelete_ScopedToVisitedDistricts()
    {
        using TempDb db = new();
        (long qsr, long _) = Ids(db);

        // a1 lives in Kadikoy, b1 in Besiktas — two different districts (İlçe).
        long a1 = Repository.UpsertLocation(db.Conn, "Istanbul", "Kadikoy", "a1", qsr)!.Value;
        long b1 = Repository.UpsertLocation(db.Conn, "Istanbul", "Besiktas", "b1", qsr)!.Value;

        // This pass (Jan 2) visited Kadikoy (a1 re-stamped) but never reached
        // Besiktas — its link dropped off the province page, so b1 wasn't re-stamped.
        SetLastSeen(db, a1, "2026-01-02T00:00:00Z");
        SetLastSeen(db, b1, "2026-01-01T00:00:00Z");

        int closed = Repository.DeactivateStaleLocations(db.Conn, qsr, "2026-01-02T00:00:00Z");

        // b1 is stale but in an unvisited district → left open, not a false closure.
        Assert.Equal(0, closed);
        Assert.True(IsActive(db, a1));
        Assert.True(IsActive(db, b1));

        // Next pass (Jan 3) actually reaches Besiktas: a sibling b2 is recorded
        // there, so the district is now visited and b1 (still stale) is closed.
        long b2 = Repository.UpsertLocation(db.Conn, "Istanbul", "Besiktas", "b2", qsr)!.Value;
        SetLastSeen(db, a1, "2026-01-03T00:00:00Z");
        SetLastSeen(db, b2, "2026-01-03T00:00:00Z");

        Assert.Equal(1, Repository.DeactivateStaleLocations(db.Conn, qsr, "2026-01-03T00:00:00Z"));
        Assert.False(IsActive(db, b1));
        Assert.True(IsActive(db, b2));
        Assert.True(IsActive(db, a1));
    }

    private static void SetLastSeen(TempDb db, long id, string iso)
    {
        using SqliteCommand cmd = db.Conn.CreateCommand();
        cmd.CommandText = "UPDATE LOCATIONS SET LAST_SEEN_AT = $t, ACTIVE = 1 WHERE ID = $id;";
        cmd.Parameters.AddWithValue("$t", iso);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static bool IsActive(TempDb db, long id)
    {
        using SqliteCommand cmd = db.Conn.CreateCommand();
        cmd.CommandText = "SELECT ACTIVE FROM LOCATIONS WHERE ID = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return Convert.ToInt64(cmd.ExecuteScalar()) == 1L;
    }
}
