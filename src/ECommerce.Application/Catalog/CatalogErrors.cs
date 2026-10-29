using ECommerce.Domain.Abstractions;

namespace ECommerce.Application.Catalog;

public static class CatalogErrors
{
    public static readonly Error CategoryNotFound =
        Error.NotFound("Catalog.CategoryNotFound", "Categoria não encontrada.");

    public static readonly Error ProductNotFound =
        Error.NotFound("Catalog.ProductNotFound", "Produto não encontrado.");

    /// <summary>Produto existe mas saiu da loja (inativo ou sem variação ativa); a API responde 410 (SEO-02).</summary>
    public static readonly Error ProductUnavailable =
        Error.NotFound("Catalog.ProductUnavailable", "Este produto não está mais disponível.");
}
