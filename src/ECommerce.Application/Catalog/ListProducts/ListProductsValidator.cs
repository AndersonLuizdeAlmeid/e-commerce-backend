using FluentValidation;

namespace ECommerce.Application.Catalog.ListProducts;

internal sealed class ListProductsValidator : AbstractValidator<ListProductsQuery>
{
    public const int SearchMaxLength = 100;
    public const int MaxFilterValues = 20;

    public ListProductsValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, ListProductsQuery.MaxPageSize);
        RuleFor(x => x.MinPrice).GreaterThanOrEqualTo(0).When(x => x.MinPrice.HasValue);
        RuleFor(x => x.MaxPrice)
            .GreaterThanOrEqualTo(x => x.MinPrice!.Value)
            .When(x => x.MinPrice.HasValue && x.MaxPrice.HasValue)
            .WithMessage("O preço máximo deve ser maior ou igual ao preço mínimo.");
        RuleFor(x => x.Search).MaximumLength(SearchMaxLength);
        RuleFor(x => x.Colors.Count).LessThanOrEqualTo(MaxFilterValues).OverridePropertyName("colors");
        RuleFor(x => x.Sizes.Count).LessThanOrEqualTo(MaxFilterValues).OverridePropertyName("sizes");
        RuleFor(x => x.Sort)
            .Must(ProductSort.IsValid)
            .WithMessage($"Ordenação inválida. Use: {string.Join(", ", ProductSort.Allowed)}.");
    }
}
