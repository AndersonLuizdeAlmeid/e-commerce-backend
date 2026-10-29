using ECommerce.Application.Catalog;

namespace ECommerce.Application.UnitTests.Catalog;

public sealed class SeoDefaultsTests
{
    [Fact, Trait("Spec", "SEO-03")]
    public void Build_WithOwnSeoFields_KeepsThem()
    {
        var seo = SeoDefaults.Build("Título SEO", "Descrição SEO", "Nome", "Descrição longa");

        seo.ShouldBe(new SeoResponse("Título SEO", "Descrição SEO"));
    }

    [Fact, Trait("Spec", "SEO-03")]
    public void Build_WithoutSeoFields_FallsBackToNameAndDescription()
    {
        var seo = SeoDefaults.Build(null, "  ", "Camiseta Básica", "Camiseta de algodão.");

        seo.ShouldBe(new SeoResponse("Camiseta Básica", "Camiseta de algodão."));
    }

    [Fact]
    public void Build_WhenDescriptionIsLong_TruncatesWithEllipsisWithinLimit()
    {
        var seo = SeoDefaults.Build(null, null, "Nome", new string('a', 300));

        seo.Description.Length.ShouldBe(SeoDefaults.DescriptionMaxLength);
        seo.Description.ShouldEndWith("…");
    }
}
