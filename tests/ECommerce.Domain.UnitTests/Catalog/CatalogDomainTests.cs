using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Catalog;

namespace ECommerce.Domain.UnitTests.Catalog;

public sealed class CatalogDomainTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    [Theory, Trait("Spec", "SEO-01")]
    [InlineData("Anel Solitário Zircônia", "anel-solitario-zirconia")]
    [InlineData("  Bolsa  &  Couro! ", "bolsa-couro")]
    [InlineData("Prata 925", "prata-925")]
    public void Slug_FromText_RemovesAccentsAndSymbols(string text, string expected)
    {
        Slug.FromText(text).Value.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Com-Maiuscula")]
    [InlineData("com espaço")]
    [InlineData("-comeca-com-hifen")]
    [InlineData("acentuação")]
    public void Slug_Invalid_Fails(string value)
    {
        Slug.Create(value).Error.ShouldBe(CatalogDomainErrors.InvalidSlug);
    }

    [Theory, Trait("Spec", "RF-056-CA4")]
    [InlineData("7891234567895")]
    [InlineData("96385074")]
    public void Gtin_WithRightCheckDigit_IsValid(string code)
    {
        Gtin.Create(code).IsSuccess.ShouldBeTrue();
    }

    [Theory, Trait("Spec", "RF-059-CA2")]
    [InlineData("7891234567890")]
    [InlineData("123")]
    [InlineData("78912345678AB")]
    [InlineData("")]
    public void Gtin_Invalid_Fails(string code)
    {
        Gtin.Create(code).Error.ShouldBe(CatalogDomainErrors.InvalidBarcode);
    }

    [Fact, Trait("Spec", "Q07")]
    public void Category_UnderSubcategory_Fails()
    {
        var main = NewCategory("Anéis");
        var sub = Category.Create(Content("Masculinos"), main, Now).Value;

        Category.Create(Content("Terceiro nível"), sub, Now).Error.ShouldBe(CatalogDomainErrors.SubcategoryDepth);
    }

    [Fact, Trait("Spec", "Q07")]
    public void Category_WithChildren_CannotBecomeSubcategory()
    {
        var category = NewCategory("Anéis");
        var other = NewCategory("Colares");

        category.Update(Content("Anéis"), other, hasChildren: true, Now).Error.ShouldBe(CatalogDomainErrors.SubcategoryDepth);
    }

    [Fact]
    public void Category_CannotBeOwnParent()
    {
        var category = NewCategory("Anéis");

        category.Update(Content("Anéis"), category, hasChildren: false, Now).Error.ShouldBe(CatalogDomainErrors.CategoryCannotBeOwnParent);
    }

    [Fact, Trait("Spec", "RN-025")]
    public void Category_Update_ReturnsPreviousSlugOnlyWhenItChanges()
    {
        var category = NewCategory("Anéis");

        category.Update(Content("Anéis"), null, false, Now).Value.ShouldBeNull();
        category.Update(Content("Anéis", "aneis-e-aliancas"), null, false, Now).Value!.Value.ShouldBe("aneis");
    }

    [Fact, Trait("Spec", "RF-050")]
    public void Category_BlankLabels_InheritAsNull_AndLongLabelsFail()
    {
        var category = Category.Create(Content("Anéis") with { ColorLabel = "  ", SizeLabel = "Aro" }, null, Now).Value;

        category.ColorLabel.ShouldBeNull();
        category.SizeLabel.ShouldBe("Aro");
        Category.Create(Content("Anéis") with { SizeLabel = new string('x', 31) }, null, Now).Error.ShouldBe(CatalogDomainErrors.InvalidVariantLabel);
    }

    [Theory, Trait("Spec", "RF-057")]
    [InlineData("#C9A55")]
    [InlineData("C9A55C")]
    [InlineData("#GGGGGG")]
    public void Color_InvalidHex_Fails(string hex)
    {
        Color.Create("Dourado", hex).Error.ShouldBe(CatalogDomainErrors.InvalidHex);
    }

    [Fact]
    public void Color_SlugComesFromName()
    {
        Color.Create("Ródio Negro", "#222222").Value.Slug.Value.ShouldBe("rodio-negro");
    }

    [Fact, Trait("Spec", "RN-023")]
    public void Product_StartsInactive_AndNeedsActiveVariantToEnterStore()
    {
        var product = NewProduct();

        product.IsActive.ShouldBeFalse();
        product.SetActive(true, Now).Error.ShouldBe(CatalogDomainErrors.ActiveProductNeedsVariant);

        product.AddVariants([new NewVariant(null, null, "UNICA")], Now);
        product.SetActive(true, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact, Trait("Spec", "RN-023")]
    public void Product_Active_CannotLoseLastActiveVariant()
    {
        var product = NewProduct();
        var variant = product.AddVariants([new NewVariant(null, null, "UNICA")], Now).Value[0];
        product.SetActive(true, Now);

        product.SetVariantActive(variant.Id, false, Now).Error.ShouldBe(CatalogDomainErrors.ActiveProductNeedsVariant);
    }

    [Fact, Trait("Spec", "RN-021")]
    public void Product_AddVariants_SkipsExistingCombination()
    {
        var color = ColorId.New();
        var size = SizeId.New();
        var product = NewProduct();
        product.AddVariants([new NewVariant(color, size, "A")], Now);

        var added = product.AddVariants([new NewVariant(color, size, "B"), new NewVariant(color, null, "C")], Now).Value;

        added.Select(v => v.Sku).ShouldBe(["C"]);
        product.Variants.Count.ShouldBe(2);
    }

    [Fact, Trait("Spec", "RF-056-CA1")]
    public void Product_DuplicateSku_Fails()
    {
        var product = NewProduct();
        product.AddVariants([new NewVariant(ColorId.New(), null, "A")], Now);

        product.AddVariants([new NewVariant(ColorId.New(), null, "a")], Now).Error.ShouldBe(CatalogDomainErrors.DuplicateSku);
    }

    [Fact, Trait("Spec", "RN-020")]
    public void Variant_NonPositivePrice_Fails_AndNullInheritsBasePrice()
    {
        var product = NewProduct();
        var variant = product.AddVariants([new NewVariant(null, null, "A")], Now).Value[0];

        product.UpdateVariant(variant.Id, new VariantChanges("A", 0m, null, null), Now).Error.ShouldBe(CatalogDomainErrors.InvalidPrice);

        product.UpdateVariant(variant.Id, new VariantChanges("A", null, null, null), Now).IsSuccess.ShouldBeTrue();
        product.EffectivePrice(variant).ShouldBe(89.90m);
    }

    [Theory, Trait("Spec", "RN-020")]
    [InlineData(0, 100)]
    [InlineData(10, 0)]
    public void Product_InvalidPriceOrPackage_Fails(double price, int weight)
    {
        var result = Product.Create(ProductData() with { BasePrice = (decimal)price, Package = new PackageSize(weight, 1, 1, 1) }, Now);

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(ErrorType.Validation);
    }

    private static Category NewCategory(string name) => Category.Create(Content(name), null, Now).Value;

    private static CategoryContent Content(string name, string? slug = null) =>
        new(name, slug is null ? Slug.FromText(name).Value : Slug.Create(slug).Value, string.Empty, null, null, null, null);

    private static Product NewProduct() => Product.Create(ProductData(), Now).Value;

    private static ProductContent ProductData() => new(
        CategoryId.New(), "Anel Solitário", Slug.Create("anel-solitario").Value, string.Empty, 89.90m, new PackageSize(20, 3, 5, 5), null, null);
}
