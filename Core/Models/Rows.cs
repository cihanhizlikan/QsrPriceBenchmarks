namespace QsrPriceBenchmarks.Core.Models;

public sealed record LocationRow(
    long Id, long QsrId, long? DistrictId, string Slug,
    string? Name, string? Address, double? Latitude, double? Longitude);

public sealed record PlatformLocationRow(
    long Id, long QsrId, long PlatformId, long? LocationId, string Slug,
    string? Name, string? Address, double? Latitude, double? Longitude);

public sealed record ScrapeRunRow(long Id, long QsrId, string StartedAt, string? FinishedAt);

/// <summary>
/// A platform location that produced no menu snapshots in its chain's most
/// recent completed scrape run. <paramref name="Url"/> is the Tıkla Gelsin page
/// URL (the practical identifier, since Name/Address are often blank when the
/// scrape failed).
/// </summary>
public sealed record UnscrapedLocationRow(string QsrName, string? Name, string? Address, string Url);

/// <summary>A per-(province, district) location tally for one chain, for the map.</summary>
public sealed record ProvinceDistrictCount(string Province, string District, int Count);

/// <summary>A geocoding result: latitude/longitude pair.</summary>
public readonly record struct GeoPoint(double Latitude, double Longitude);
