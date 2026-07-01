using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Core.Scraping;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// Regression for Popeyes Step-1: district navigation links (repeated as a menu
/// on every district page) were stored as LOCATIONS with NULL name/address.
/// A card only counts as a restaurant when it has a name AND a real address, and
/// that rule must be applied to district-page cards too — pinned here.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ChainCrawlerCardFilterTests
{
    private static RestaurantCard Card(string? name, string? address) =>
        new("https://example.com/restoranlar/istanbul/x", name, address);

    [Theory(DisplayName = "Only a named card with a real (numbered) address is a restaurant")]
    [InlineData("Avcılar Şubesi", "Merkez Mah. Marmara Cad. No: 19", true)]  // real restaurant
    [InlineData(null, null, false)]                       // district nav link ("+", now nulled)
    [InlineData("Bağcılar", null, false)]                 // district name, no address
    [InlineData("Kampanya", "Hemen sipariş ver!", false)] // slogan, no street number
    [InlineData("Şube", "", false)]                       // empty address
    public void IsRestaurantCard(string? name, string? address, bool expected) =>
        Assert.Equal(expected, ChainCrawler.IsRestaurantCard(Card(name, address)));
}
