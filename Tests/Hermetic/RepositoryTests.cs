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
        using var cmd = db.Conn.CreateCommand();
        cmd.CommandText = "SELECT NAME, ADDRESS, LATITUDE, LONGITUDE FROM LOCATIONS WHERE SLUG=$s AND QSR_ID=$q;";
        cmd.Parameters.AddWithValue("$s", slug);
        cmd.Parameters.AddWithValue("$q", qsr);
        using var r = cmd.ExecuteReader();
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
        using var db = new TempDb();
        Assert.Throws<InvalidOperationException>(() => Repository.QsrId(db.Conn, "Nonexistent"));
        Assert.Throws<InvalidOperationException>(() => Repository.PlatformId(db.Conn, "Nonexistent"));
    }

    [Fact(DisplayName = "UpsertLocation is idempotent and skips 'test' slugs")]
    public void UpsertLocation_IdempotentAndTestSkip()
    {
        using var db = new TempDb();
        var (qsr, _) = Ids(db);

        var id1 = Repository.UpsertLocation(db.Conn, "Istanbul", "Kadikoy", "kadikoy-1", qsr);
        var id2 = Repository.UpsertLocation(db.Conn, "Istanbul", "Kadikoy", "kadikoy-1", qsr);

        Assert.NotNull(id1);
        Assert.Equal(id1, id2);
        Assert.Null(Repository.UpsertLocation(db.Conn, "Istanbul", "Kadikoy", "test-slug", qsr));
    }

    [Fact(DisplayName = "UpdateLocationDetails nulls coords only when the ADDRESS changes")]
    public void UpdateLocationDetails_CoordInvalidation()
    {
        using var db = new TempDb();
        var (qsr, _) = Ids(db);
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
        using var db = new TempDb();
        var (qsr, _) = Ids(db);
        Repository.UpsertLocation(db.Conn, "Istanbul", "Kadikoy", "k2", qsr);

        Repository.UpdateLocationDetails(db.Conn, "k2", "Lezzet Durağı", "Her An Yanında!", qsr);

        var row = ReadLocation(db, "k2", qsr);
        Assert.Equal("Lezzet Durağı", row.Name);
        Assert.Null(row.Address); // slogan dropped, not stored
    }

    [Fact(DisplayName = "UpsertPlatformLocationsBulk inserts new, skips test + existing, counts correctly")]
    public void BulkUpsert_Counts()
    {
        using var db = new TempDb();
        var (qsr, plat) = Ids(db);

        var inserted = Repository.UpsertPlatformLocationsBulk(
            db.Conn, qsr, plat, new[] { "a", "b", "test-x", "c" });
        Assert.Equal(3, inserted);

        // Re-running with overlapping + new slugs inserts only the genuinely new one.
        var inserted2 = Repository.UpsertPlatformLocationsBulk(
            db.Conn, qsr, plat, new[] { "a", "b", "c", "d" });
        Assert.Equal(1, inserted2);

        Assert.Equal(4L, Convert.ToInt64(db.Scalar(
            "SELECT COUNT(*) FROM PLATFORM_LOCATIONS WHERE QSR_ID=$q AND PLATFORM_ID=$p;",
            ("$q", qsr), ("$p", plat))));
    }

    [Fact(DisplayName = "UpdatePlatformLocation nulls coords when ADDRESS changes")]
    public void UpdatePlatformLocation_AddressChange_NullsCoords()
    {
        using var db = new TempDb();
        var (qsr, plat) = Ids(db);
        var plId = Repository.UpsertPlatformLocation(db.Conn, qsr, plat, "pl1")!.Value;

        Repository.UpdatePlatformLocation(db.Conn, plId, address: "Eski Adres No 1");
        Repository.UpdatePlatformLocation(db.Conn, plId, lat: 40.0, lon: 29.0);
        Repository.UpdatePlatformLocation(db.Conn, plId, address: "Yeni Adres No 2");

        using var cmd = db.Conn.CreateCommand();
        cmd.CommandText = "SELECT LATITUDE, LONGITUDE FROM PLATFORM_LOCATIONS WHERE ID=$id;";
        cmd.Parameters.AddWithValue("$id", plId);
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.True(r.IsDBNull(0));
        Assert.True(r.IsDBNull(1));
    }

    [Fact(DisplayName = "GetOrCreateMenuItem is idempotent per (qsr, name)")]
    public void GetOrCreateMenuItem_Idempotent()
    {
        using var db = new TempDb();
        var (qsr, _) = Ids(db);
        var a = Repository.GetOrCreateMenuItem(db.Conn, qsr, "Whopper");
        var b = Repository.GetOrCreateMenuItem(db.Conn, qsr, "Whopper");
        Assert.Equal(a, b);
    }

    [Fact(DisplayName = "Scrape run lifecycle: start, snapshot, finish, list, delete")]
    public void ScrapeRun_Lifecycle()
    {
        using var db = new TempDb();
        var (qsr, plat) = Ids(db);
        var plId = Repository.UpsertPlatformLocation(db.Conn, qsr, plat, "pl1")!.Value;
        var item = Repository.GetOrCreateMenuItem(db.Conn, qsr, "Whopper");

        var run = Repository.StartScrapeRun(db.Conn, qsr);
        Repository.InsertSnapshot(db.Conn, run, plId, item, 100m);
        Repository.FinishScrapeRun(db.Conn, run);

        Assert.Contains(Repository.ListScrapeRuns(db.Conn), x => x.Id == run && x.FinishedAt is not null);

        var (found, qsrId, _, count) = Repository.GetScrapeRunForDeletion(db.Conn, run);
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
        using var db = new TempDb();
        var (qsr, _) = Ids(db);
        var r1 = Repository.StartScrapeRun(db.Conn, qsr);
        var r2 = Repository.StartScrapeRun(db.Conn, qsr);
        var r3 = Repository.StartScrapeRun(db.Conn, qsr);

        var affected = Repository.DeleteScrapeRunsBefore(db.Conn, r3);

        Assert.Equal(2, affected); // r1 + r2
        var remaining = Repository.ListScrapeRuns(db.Conn).Select(x => x.Id).ToList();
        Assert.DoesNotContain(r1, remaining);
        Assert.DoesNotContain(r2, remaining);
        Assert.Contains(r3, remaining);
    }

    [Fact(DisplayName = "Logo blob round-trips; null before set")]
    public void LogoBlob_RoundTrip()
    {
        using var db = new TempDb();
        var (qsr, _) = Ids(db);

        Assert.Null(Repository.GetLogoBlob(db.Conn, qsr));
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        Repository.SetLogoBlob(db.Conn, qsr, bytes);
        Assert.Equal(bytes, Repository.GetLogoBlob(db.Conn, qsr));
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
