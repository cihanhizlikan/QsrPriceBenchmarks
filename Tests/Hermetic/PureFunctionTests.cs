using QsrPriceBenchmarks.Core.Util;
using Xunit;

namespace QsrPriceBenchmarks.IntegrationTests.Hermetic;

[Trait("Category", "Unit")]
public sealed class PriceParsingTests
{
    [Theory(DisplayName = "Turkish-formatted prices parse to the right decimal")]
    [InlineData("340,00 TL", 340.00)]
    [InlineData("340 TL", 340)]
    [InlineData("1.250,50 TL", 1250.50)]
    [InlineData("1.234.567,89", 1234567.89)]
    [InlineData("₺99,90", 99.90)]
    [InlineData("  450,5 TL ", 450.5)]
    public void ParsePrice_TurkishFormats(string raw, decimal expected) =>
        Assert.Equal(expected, PriceParsing.ParsePrice(raw));

    [Theory(DisplayName = "Non-numeric / empty input yields null")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Fiyat yok")]
    public void ParsePrice_NoNumber_ReturnsNull(string? raw) =>
        Assert.Null(PriceParsing.ParsePrice(raw));
}

[Trait("Category", "Unit")]
public sealed class TextNormalizationTests
{
    [Fact(DisplayName = "Title() applies Turkish-correct casing (i -> İ)")]
    public void Title_TurkishCasing()
    {
        // Requires InvariantGlobalization=false. The Turkish uppercase of 'i'
        // is the dotted capital 'İ' (U+0130) — this is the whole reason the
        // project pins tr-TR rather than invariant casing.
        Assert.Equal("İstanbul", TextNormalization.Title("istanbul"));
        Assert.Equal("Burger King", TextNormalization.Title("burger king"));
    }

    [Theory(DisplayName = "Title() returns null for empty/whitespace, never \"\"")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Title_Empty_ReturnsNull(string? raw) =>
        Assert.Null(TextNormalization.Title(raw));

    [Fact(DisplayName = "StripBoilerplate cuts trailing phone/hours markers")]
    public void StripBoilerplate_CutsMarkers()
    {
        Assert.Equal("BK Kadıköy",
            TextNormalization.StripBoilerplate("BK Kadıköy Telefon: 0212 000 00 00"));
        Assert.Equal("Sube",
            TextNormalization.StripBoilerplate("Sube  Çalışma Saatleri 09:00-23:00"));
    }

    [Theory(DisplayName = "LooksLikeSlogan: no digits AND ends with '!'")]
    [InlineData("Usta Dönerci Lezzetleri Her An Yanında!", true)]
    [InlineData("Moda Cad. No: 5", false)]   // has a digit
    [InlineData("Merkez Şube", false)]        // no '!'
    [InlineData("", false)]
    public void LooksLikeSlogan(string addr, bool expected) =>
        Assert.Equal(expected, TextNormalization.LooksLikeSlogan(addr));

    [Theory(DisplayName = "IsRealAddress requires at least one digit")]
    [InlineData("Caferağa Mah. No: 12", true)]
    [InlineData("Kadıköy Merkez", false)]
    [InlineData(null, false)]
    public void IsRealAddress(string? addr, bool expected) =>
        Assert.Equal(expected, TextNormalization.IsRealAddress(addr));

    [Theory(DisplayName = "IsTestSlug matches 'test' case-insensitively")]
    [InlineData("foo-test-bar", true)]
    [InlineData("TESTburger", true)]
    [InlineData("kadikoy-1", false)]
    public void IsTestSlug(string slug, bool expected) =>
        Assert.Equal(expected, TextNormalization.IsTestSlug(slug));

    [Theory(DisplayName = "IsPlausibleName rejects '+', symbols, and bare numbers")]
    [InlineData("+", false)]
    [InlineData("-", false)]
    [InlineData("3", false)]
    [InlineData("12", false)]
    [InlineData(" ", false)]
    [InlineData(null, false)]
    [InlineData("BK", true)]
    [InlineData("Yüreğir", true)]
    [InlineData("Arnavutköy Şubesi", true)]
    public void IsPlausibleName(string? text, bool expected) =>
        Assert.Equal(expected, TextNormalization.IsPlausibleName(text));

    [Theory(DisplayName = "IsPlausibleAddress rejects numeric badges, accepts real addresses")]
    [InlineData("1", false)]
    [InlineData("6", false)]
    [InlineData("+", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("No: 5", true)]
    [InlineData("Atatürk Cad. No: 12 Seyhan", true)]
    public void IsPlausibleAddress(string? address, bool expected) =>
        Assert.Equal(expected, TextNormalization.IsPlausibleAddress(address));

    [Fact(DisplayName = "StripAddressSuffix removes a trailing address from a name")]
    public void StripAddressSuffix_Removes()
    {
        Assert.Equal("BK Kadıköy",
            TextNormalization.StripAddressSuffix("BK Kadıköy Moda Cad", "Moda Cad"));
        // No suffix match → name unchanged.
        Assert.Equal("BK Kadıköy",
            TextNormalization.StripAddressSuffix("BK Kadıköy", "Bağdat Cad"));
    }
}
