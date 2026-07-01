namespace QsrPriceBenchmarks.Core.Models;

/// <summary>One restaurant link discovered while crawling a chain's website.</summary>
public sealed record RestaurantCard(string Url, string? Name, string? Address);
