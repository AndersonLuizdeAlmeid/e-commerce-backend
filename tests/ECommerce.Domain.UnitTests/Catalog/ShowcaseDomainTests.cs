using ECommerce.Domain.Catalog;

namespace ECommerce.Domain.UnitTests.Catalog;

public sealed class ShowcaseDomainTests
{
    private static readonly DateTime Now = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Monday = new(2026, 11, 2);

    private static CollectionContent Eles(string slug = "eles", string name = "Eles") =>
        new(name, Slug.Create(slug).Value, "Para eles.", null, null, CardTheme.Graphite, 10);

    private static OccasionContent DiaDosPais(params Guid[] productIds) =>
        new("Dia dos Pais", "Presentes para ele.", Monday, Monday.AddDays(7), true, productIds);

    [Fact, Trait("Spec", "RF-024")]
    public void Collection_New_IsActiveWithTheChosenTheme()
    {
        var collection = Collection.Create(Eles(), Now).Value;

        collection.IsActive.ShouldBeTrue();
        collection.CardTheme.ShouldBe(CardTheme.Graphite);
        collection.Position.ShouldBe(10);
    }

    [Theory, Trait("Spec", "RF-024")]
    [InlineData("A")]
    [InlineData("  ")]
    public void Collection_WithShortName_Fails(string name)
    {
        Collection.Create(Eles(name: name), Now).Error.ShouldBe(CatalogDomainErrors.InvalidName);
    }

    [Fact, Trait("Spec", "RF-024-CA4")]
    public void Collection_Update_ReturnsPreviousSlugOnlyWhenItChanged()
    {
        var collection = Collection.Create(Eles(), Now).Value;

        collection.Update(Eles(), Now).Value.ShouldBeNull();
        collection.Update(Eles("linha-masculina"), Now).Value!.Value.ShouldBe("eles");
    }

    [Fact, Trait("Spec", "RF-061-CA4")]
    public void Collection_ReplaceImage_ReturnsPreviousKeyToDeleteFromBucket()
    {
        var collection = Collection.Create(Eles(), Now).Value;

        collection.ReplaceImage("https://img/a.webp", "a.webp", Now).ShouldBeNull();
        collection.ReplaceImage("https://img/b.webp", "b.webp", Now).ShouldBe("a.webp");
        collection.ReplaceImage(null, null, Now).ShouldBe("b.webp");
        collection.ImageUrl.ShouldBeNull();
    }

    [Fact, Trait("Spec", "RF-025")]
    public void Occasion_RemovesRepeatedProductsKeepingTheOrder()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        var occasion = Occasion.Create(DiaDosPais(second, first, second), Now).Value;

        occasion.ProductIds.ShouldBe([second, first]);
    }

    [Fact, Trait("Spec", "RF-025")]
    public void Occasion_CanStartAndEndOnTheSameDay()
    {
        var content = DiaDosPais() with { EndsOn = Monday };

        Occasion.Create(content, Now).IsSuccess.ShouldBeTrue();
    }

    [Fact, Trait("Spec", "RF-025")]
    public void Occasion_EndingBeforeItStarts_Fails()
    {
        var content = DiaDosPais() with { EndsOn = Monday.AddDays(-1) };

        Occasion.Create(content, Now).Error.ShouldBe(CatalogDomainErrors.InvalidOccasionPeriod);
    }

    [Theory, Trait("Spec", "RF-025")]
    [InlineData(160, true)]
    [InlineData(161, false)]
    public void Occasion_TextHasUpTo160Characters(int length, bool valid)
    {
        var content = DiaDosPais() with { Text = new string('x', length) };

        Occasion.Create(content, Now).IsSuccess.ShouldBe(valid);
    }
}
