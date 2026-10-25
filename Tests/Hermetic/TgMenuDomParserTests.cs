using AngleSharp.Dom;
using QsrPriceBenchmarks.Core.Models;
using QsrPriceBenchmarks.Core.Scraping;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

/// <summary>
/// The live-DOM side of a Tıkla Gelsin menu tab, on markup trimmed from a real
/// page (Arby's, Sandviçler tab, 2026-10-25): one grid per sub-category, cards
/// <c>product-card-N</c> with <c>-title</c>, <c>-description</c> and <c>-price</c>.
/// These parse static HTML through AngleSharp — no network or browser.
/// </summary>
[Trait("Category", "Unit")]
public sealed class TgMenuDomParserTests
{
    private const string Url = "https://www.tiklagelsin.com/restoran/arbys/prime-mall-avm-s-73319643?gel-al";

    private static string Card(int n, string title, string description, string price)
    {
        return $"""
            <div id="product-card-{n}" class="w-full flex flex-row bg-bw-light5 rounded-2xl gap-1">
              <div class="flex-1 flex flex-col bg-white p-[6px]"><div class="flex flex-col h-full justify-between">
                <div class="h-full sm:h-[70px] leading-tight">
                  <h3 id="product-card-{n}-title" class="text-bw-default text-sm line-clamp-1">{title}</h3>
                  <div id="product-card-{n}-description" class="text-bw-light2 text-xs">{description}</div>
                </div>
                <div class="flex justify-between items-center h-3 mb-1 sm:mb-3">
                  <div id="product-card-{n}-price" class="whitespace-nowrap mt-1"><div class="flex flex-col-reverse">
                    <span class="font-bold leading-tight text-sm sm:text-body2 text-marker">{price}</span>
                  </div></div>
                  <button id="product-card-{n}-add-button"><span class="ml-2">Ekle</span></button>
                </div>
              </div></div>
            </div>
            """;
    }

    private static async Task<List<MenuItem>> ParseAsync(string body)
    {
        IDocument doc = await PageFetcher.ParseHtmlAsync($"<html><body>{body}</body></html>", Url);
        return TgMenuDomParser.Parse(doc);
    }

    private static readonly string SandwichTab = $"""
        <div>
          <h2 class="text-sm font-bold">Et Sandviçler</h2>
          <div class="grid lg:grid-cols-2 gap-3 pb-4">
            {Card(1, "Beef'n Cheddar", "2 Adet Roast Beef dilimi", "215,00 TL")}
            {Card(2, "Roast Beef Deluxe", "Roast beef, domates, marul", "230,00 TL")}
          </div>
          <h2 class="text-sm font-bold">Tavuk Sandviçler</h2>
          <div class="grid lg:grid-cols-2 gap-3 pb-4">
            {Card(3, "Golden Curly Chicken Burger", "Tavuk göğsü, cheddar", "199,50 TL")}
          </div>
        </div>
        """;

    [Fact(DisplayName = "Every sub-category grid on the tab is read, not only the first")]
    public async Task Parse_ReadsEverySubCategoryGrid()
    {
        List<MenuItem> items = await ParseAsync(SandwichTab);

        Assert.Equal(3, items.Count);
        Assert.Contains(items, i => i.Name == "Golden Curly Chicken Burger");
    }

    [Fact(DisplayName = "Each card's name comes from its title and its price from its own price element")]
    public async Task Parse_PairsTitleWithItsOwnPrice()
    {
        List<MenuItem> items = await ParseAsync(SandwichTab);

        Assert.Equal(215.00m, items.Single(i => i.Name == "Beef'n Cheddar").Price);
        Assert.Equal(230.00m, items.Single(i => i.Name == "Roast Beef Deluxe").Price);
        Assert.Equal(199.50m, items.Single(i => i.Name == "Golden Curly Chicken Burger").Price);
        // The description holds portion text ("2 Adet …") and must never become a name.
        Assert.DoesNotContain(items, i => i.Name.Contains("Adet", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "A card without a readable price is skipped, not stored with a guess")]
    public async Task Parse_SkipsCardWithoutPrice()
    {
        List<MenuItem> items = await ParseAsync($"""
            <div class="grid lg:grid-cols-2 gap-3 pb-4">
              {Card(1, "Fiyatsız Ürün", "", "")}
              {Card(2, "Tavukburger", "", "140,00 TL")}
            </div>
            """);

        MenuItem only = Assert.Single(items);
        Assert.Equal("Tavukburger", only.Name);
    }
}
