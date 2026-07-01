using QsrPriceBenchmarks.Core.Scraping;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

[Trait("Category", "Unit")]
public sealed class UrlUtilsTests
{
    [Fact(DisplayName = "SplitPath drops empties")]
    public void SplitPath_DropsEmpties() =>
        Assert.Equal(new[] { "tr", "restoranlar" },
            UrlUtils.SplitPath("/tr/restoranlar/").ToArray());

    [Fact(DisplayName = "UrlToParts: 3 segments after root -> (province, district, slug)")]
    public void UrlToParts_ThreeSegments()
    {
        var (province, district, slug) = UrlUtils.UrlToParts(
            "https://x.com/sube/istanbul/kadikoy/bk-1", "https://x.com/sube");
        Assert.Equal("istanbul", province);
        Assert.Equal("kadikoy", district);
        Assert.Equal("bk-1", slug);
    }

    [Fact(DisplayName = "UrlToParts: 2 segments after root -> province used as district")]
    public void UrlToParts_TwoSegments()
    {
        var (province, district, slug) = UrlUtils.UrlToParts(
            "https://x.com/sube/istanbul/bk-1", "https://x.com/sube");
        Assert.Equal("istanbul", province);
        Assert.Equal("istanbul", district);
        Assert.Equal("bk-1", slug);
    }

    [Fact(DisplayName = "UrlToParts: off-tree URL throws")]
    public void UrlToParts_OffTree_Throws() =>
        Assert.Throws<ArgumentException>(() =>
            UrlUtils.UrlToParts("https://x.com/other/istanbul/bk-1", "https://x.com/sube"));

    [Fact(DisplayName = "AbsUrl resolves against a <base href> tag when present")]
    public async Task AbsUrl_UsesBaseTag()
    {
        const string pageUrl = "https://x.com/page";
        var doc = await PageFetcher.ParseHtmlAsync(
            "<html><head><base href=\"https://x.com/base/\"></head><body></body></html>", pageUrl);

        // Relative href must resolve against the <base>, not the page URL.
        Assert.Equal("https://x.com/base/foo", UrlUtils.AbsUrl(doc, "foo", pageUrl));
    }

    [Fact(DisplayName = "ParseDepthLinks keeps only same-host links exactly one level deeper")]
    public async Task ParseDepthLinks_FiltersByDepthAndHost()
    {
        const string root = "https://x.com/r";
        var html = """
            <html><body>
              <a href="/r/a">depth+1 (keep)</a>
              <a href="/r/a/b">depth+2 (drop)</a>
              <a href="/x/a">off-tree prefix (drop)</a>
              <a href="https://other.com/r/a">other host (drop)</a>
            </body></html>
            """;
        var doc = await PageFetcher.ParseHtmlAsync(html, root);

        var links = UrlUtils.ParseDepthLinks(doc, root, depthOffset: 1);

        Assert.Equal(new[] { "https://x.com/r/a/" }, links.ToArray());
    }
}
