namespace QsrPriceBenchmarks.Core.Models;

/// <summary>One scraped menu item: its display name and price in TRY.</summary>
public readonly record struct MenuItem(string Name, decimal Price);
