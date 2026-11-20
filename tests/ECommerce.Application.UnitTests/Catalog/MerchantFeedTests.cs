using System.Xml.Linq;
using ECommerce.Application.Catalog.GetMerchantFeed;

namespace ECommerce.Application.UnitTests.Catalog;

/// <summary>Fase 6.6: regras puras do feed do Merchant Center (SEO-14).</summary>
public sealed class MerchantFeedTests
{
    [Theory, Trait("Spec", "SEO-14")]
    [InlineData("4006381333931")] // EAN-13
    [InlineData("7891234567895")] // EAN-13 brasileiro
    [InlineData("96385074")] // EAN-8
    [InlineData("036000291452")] // UPC-A
    [InlineData("10012345678902")] // GTIN-14
    public void Gtin_WithTheRightCheckDigit_IsKept(string code) => Gtin.Normalize(code).ShouldBe(code);

    [Theory, Trait("Spec", "SEO-14")]
    [InlineData("4006381333932")] // verificador errado
    [InlineData("12345")]
    [InlineData("ABCDEFGH")]
    [InlineData(null)]
    [InlineData("")]
    public void Gtin_Invalid_IsLeftOut(string? code) => Gtin.Normalize(code).ShouldBeNull();

    [Fact, Trait("Spec", "SEO-14")]
    public void Xml_IsValidRss_WithEscapedTextAndGoogleFields()
    {
        var feed = new MerchantFeed("MO Acessórios", "https://loja.com.br", "MO Acessórios",
        [
            new MerchantFeedItem(
                "ANEL-01", "group-1", "Anel <Brilho> & Cia - Dourado", "Banho de ouro 18k \"premium\"", "https://loja.com.br/produto/anel?cor=dourado&tamanho=16",
                "https://fotos.loja.com.br/a.jpg", ["https://fotos.loja.com.br/b.jpg"], true, 89.9m, "7891234567895", "Dourado", "16", "Anéis", "female"),
            new MerchantFeedItem(
                "ANEL-02", "group-1", "Anel - Prateado", "Prata", "https://loja.com.br/produto/anel?cor=prateado",
                "https://fotos.loja.com.br/c.jpg", [], false, 1234.5m, null, "Prateado", null, "Acessórios > Anéis", "male"),
        ]);

        var xml = feed.ToXml();

        xml.ShouldStartWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        var document = XDocument.Parse(xml);
        XNamespace g = "http://base.google.com/ns/1.0";
        var items = document.Descendants("item").ToList();
        items.Count.ShouldBe(2);
        items[0].Element("title")!.Value.ShouldBe("Anel <Brilho> & Cia - Dourado");
        items[0].Element("link")!.Value.ShouldBe("https://loja.com.br/produto/anel?cor=dourado&tamanho=16");
        items[0].Element(g + "price")!.Value.ShouldBe("89.90 BRL");
        items[0].Element(g + "gtin")!.Value.ShouldBe("7891234567895");
        items[0].Element(g + "additional_image_link")!.Value.ShouldBe("https://fotos.loja.com.br/b.jpg");
        items[0].Element(g + "availability")!.Value.ShouldBe("in_stock");
        items[0].Element(g + "age_group")!.Value.ShouldBe("adult");
        items[1].Element(g + "price")!.Value.ShouldBe("1234.50 BRL");
        items[1].Element(g + "identifier_exists")!.Value.ShouldBe("no");
        items[1].Element(g + "availability")!.Value.ShouldBe("out_of_stock");
        items[1].Element(g + "size").ShouldBeNull();
        items[1].Element(g + "gender")!.Value.ShouldBe("male");
    }
}
