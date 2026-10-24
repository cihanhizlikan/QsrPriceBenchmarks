using AngleSharp.Dom;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Core.Scraping;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// Regression tests for two real data bugs seen in a full --force-crawl:
///   • expand "+" toggle links were stored as restaurant names, and
///   • a numeric badge ("1", "2"…) was stored as the address (Usta Dönerci).
/// These parse static HTML through AngleSharp — no network or browser.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RestaurantCardParserTests
{
    private const string Base = "https://example.com/restoranlar";

    private static async Task<List<RestaurantCard>> ParseAsync(string bodyHtml)
    {
        IDocument doc = await PageFetcher.ParseHtmlAsync($"<html><body>{bodyHtml}</body></html>", Base);
        return RestaurantCardParser.Parse(doc, Base);
    }

    [Fact(DisplayName = "An expand '+' link is never recorded as a name")]
    public async Task PlusToggle_IsNotAName()
    {
        RestaurantCard card = (await ParseAsync("<a href=\"/restoranlar/istanbul/arnavutkoy\">+</a>")).Single();
        Assert.Null(card.Name);    // was "+" before the fix
        Assert.Null(card.Address);
    }

    [Fact(DisplayName = "Numeric badge <p> is skipped; the real address <p> is used")]
    public async Task NumericBadge_IsNotAddress()
    {
        // Usta Dönerci style: a count badge "<p>2</p>" precedes the real address.
        RestaurantCard card = (await ParseAsync("""
            <a href="/restoranlar/adana/seyhan">
              <h3>Seyhan Şube</h3>
              <p>2</p>
              <p>Atatürk Cad. No: 12 Seyhan</p>
            </a>
            """)).Single();

        Assert.Equal("Seyhan Şube", card.Name);
        Assert.Equal("Atatürk Cad. No: 12 Seyhan", card.Address); // not "2"
    }

    [Fact(DisplayName = "Only a numeric <p> yields a null address, never the number")]
    public async Task OnlyNumericParagraph_NullAddress()
    {
        RestaurantCard card = (await ParseAsync("""
            <a href="/restoranlar/adana/yuregir">
              <h3>Yüreğir</h3>
              <p>1</p>
            </a>
            """)).Single();

        Assert.Equal("Yüreğir", card.Name);
        Assert.Null(card.Address); // was "1" before the fix
    }

    [Fact(DisplayName = "A well-formed card extracts both name and address")]
    public async Task WellFormedCard_Extracted()
    {
        RestaurantCard card = (await ParseAsync("""
            <a href="/restoranlar/istanbul/arnavutkoy-subesi">
              <h3>Arnavutköy Şubesi</h3>
              <p>Eski Edirne Asfaltı No: 1211/A</p>
            </a>
            """)).Single();

        Assert.Equal("Arnavutköy Şubesi", card.Name);
        Assert.Equal("Eski Edirne Asfaltı No: 1211/A", card.Address);
    }

    [Fact(DisplayName = "A title/address-id card (Burger King style) takes name and address from those elements")]
    public async Task IdCard_NameAndAddressFromTheirElements()
    {
        RestaurantCard card = (await ParseAsync("""
            <a href="/restoranlar/istanbul/besiktas">
              <div id="restaurantCardTitle">Beşiktaş</div>
              <div id="restaurantCardAddress">Barbaros Bulvarı No: 3</div>
            </a>
            """)).Single();

        // Not the link's whole concatenated text, which a later strategy would take.
        Assert.Equal("Beşiktaş", card.Name);
        Assert.Equal("Barbaros Bulvarı No: 3", card.Address);
    }

    [Fact(DisplayName = "A bare link in a container (Popeyes style) takes its address from a sibling")]
    public async Task ContainerCard_AddressFromSibling()
    {
        RestaurantCard card = (await ParseAsync("""
            <ul><li>
              <a href="/restoranlar/istanbul/kadikoy">Kadıköy</a>
              <span>Caferağa Mahallesi No: 5</span>
            </li></ul>
            """)).Single();

        Assert.Equal("Kadıköy", card.Name);
        Assert.Equal("Caferağa Mahallesi No: 5", card.Address);
    }
}
