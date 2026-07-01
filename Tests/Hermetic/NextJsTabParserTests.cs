using System.Text.Json;
using QsrPriceBenchmarks.Core.Scraping;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

[Trait("Category", "Unit")]
public sealed class NextJsTabParserTests
{
    // The component-tree JSON exactly as it appears AFTER the parser unescapes
    // the self.__next_f.push string literal: a pb-2 section heading, then
    // (title, price) object pairs the walker pairs up.
    private const string InnerJson =
        "[" +
        "{\"className\":\"pb-2\",\"children\":\"Popüler Ürünler\"}," +
        "{\"id\":\"restaurant-card-title\",\"children\":\"Whopper\"}," +
        "{\"className\":\"font-bold text-errorText\",\"children\":\"340,00 TL\"}," +
        "{\"id\":\"restaurant-card-title\",\"children\":\"King Menü\"}," +
        "{\"className\":\"font-bold text-marker\",\"children\":\"450,50 TL\"}" +
        "]";

    /// <summary>
    /// Re-create the page shape the parser reads: the JSON above embedded as a
    /// JS string literal inside self.__next_f.push([1, "..."]). JsonSerializer
    /// produces exactly the escaping Next.js uses (and that the parser reverses).
    /// </summary>
    private static string BuildHtml(string innerJson)
    {
        var escaped = JsonSerializer.Serialize(innerJson); // includes the surrounding quotes
        return $"<!doctype html><html><body><script>self.__next_f.push([1, {escaped}])</script></body></html>";
    }

    [Fact(DisplayName = "Parser extracts (name, price) pairs from the SSR payload, in order")]
    public void Parse_ExtractsItems()
    {
        var items = NextJsTabParser.Parse(BuildHtml(InnerJson), tabName: null);

        Assert.Equal(2, items.Count);
        Assert.Equal("Whopper", items[0].Name);
        Assert.Equal(340.00m, items[0].Price);
        Assert.Equal("King Menü", items[1].Name);
        Assert.Equal(450.50m, items[1].Price);
    }

    [Fact(DisplayName = "tabName filter keeps only items under a matching section heading")]
    public void Parse_FiltersByTabName()
    {
        var html = BuildHtml(InnerJson);

        Assert.Equal(2, NextJsTabParser.Parse(html, tabName: "Popüler Ürünler").Count);
        Assert.Empty(NextJsTabParser.Parse(html, tabName: "Kampanyalar"));
    }

    [Fact(DisplayName = "Blocks without product cards are ignored")]
    public void Parse_NoCards_ReturnsEmpty()
    {
        var html = "<html><body><script>self.__next_f.push([1, \"1:[\\\"no cards here\\\"]\"])</script></body></html>";
        Assert.Empty(NextJsTabParser.Parse(html, tabName: null));
    }
}
