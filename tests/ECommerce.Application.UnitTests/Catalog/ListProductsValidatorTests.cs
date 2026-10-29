using ECommerce.Application.Catalog.ListProducts;

namespace ECommerce.Application.UnitTests.Catalog;

public sealed class ListProductsValidatorTests
{
    private readonly ListProductsValidator _validator = new();

    private static ListProductsQuery ValidQuery() => new(
        Page: 1,
        PageSize: ListProductsQuery.DefaultPageSize,
        Category: null,
        MinPrice: null,
        MaxPrice: null,
        Search: null,
        Colors: [],
        Sizes: [],
        Sort: ListProductsQuery.DefaultSort);

    [Fact]
    public void Validate_WithDefaults_IsValid()
    {
        _validator.Validate(ValidQuery()).IsValid.ShouldBeTrue();
    }

    [Theory, Trait("Spec", "RF-020")]
    [InlineData(0)]
    [InlineData(101)]
    public void Validate_WhenPageSizeOutOfRange_FailsOnPageSize(int pageSize)
    {
        var result = _validator.Validate(ValidQuery() with { PageSize = pageSize });

        result.Errors.Select(e => e.PropertyName).ShouldContain(nameof(ListProductsQuery.PageSize));
    }

    [Fact]
    public void Validate_WhenPageIsZero_FailsOnPage()
    {
        var result = _validator.Validate(ValidQuery() with { Page = 0 });

        result.Errors.Select(e => e.PropertyName).ShouldContain(nameof(ListProductsQuery.Page));
    }

    [Fact]
    public void Validate_WhenMaxPriceBelowMinPrice_FailsOnMaxPrice()
    {
        var result = _validator.Validate(ValidQuery() with { MinPrice = 100m, MaxPrice = 50m });

        result.Errors.Select(e => e.PropertyName).ShouldContain(nameof(ListProductsQuery.MaxPrice));
    }

    [Fact]
    public void Validate_WhenMinPriceIsNegative_FailsOnMinPrice()
    {
        var result = _validator.Validate(ValidQuery() with { MinPrice = -1m });

        result.Errors.Select(e => e.PropertyName).ShouldContain(nameof(ListProductsQuery.MinPrice));
    }

    [Theory]
    [InlineData("price")]
    [InlineData("price:ASC")]
    [InlineData("stock:desc")]
    [InlineData("name; DROP TABLE catalog.products")]
    public void Validate_WhenSortIsNotWhitelisted_FailsOnSort(string sort)
    {
        var result = _validator.Validate(ValidQuery() with { Sort = sort });

        result.Errors.Select(e => e.PropertyName).ShouldContain(nameof(ListProductsQuery.Sort));
    }

    [Theory]
    [InlineData("createdAt:desc")]
    [InlineData("createdAt:asc")]
    [InlineData("price:asc")]
    [InlineData("price:desc")]
    [InlineData("name:asc")]
    [InlineData("name:desc")]
    public void Validate_WhenSortIsWhitelisted_IsValid(string sort)
    {
        _validator.Validate(ValidQuery() with { Sort = sort }).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void Validate_WhenSearchIsTooLong_FailsOnSearch()
    {
        var result = _validator.Validate(ValidQuery() with { Search = new string('a', ListProductsValidator.SearchMaxLength + 1) });

        result.Errors.Select(e => e.PropertyName).ShouldContain(nameof(ListProductsQuery.Search));
    }

    [Fact]
    public void Validate_WhenTooManyColors_FailsOnColors()
    {
        var colors = Enumerable.Range(0, ListProductsValidator.MaxFilterValues + 1).Select(i => $"cor-{i}").ToArray();

        var result = _validator.Validate(ValidQuery() with { Colors = colors });

        result.Errors.Select(e => e.PropertyName).ShouldContain("colors");
    }
}
