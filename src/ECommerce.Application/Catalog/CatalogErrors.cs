using ECommerce.Domain.Abstractions;

namespace ECommerce.Application.Catalog;

public static class CatalogErrors
{
    public static readonly Error CategoryNotFound =
        Error.NotFound("Catalog.CategoryNotFound", "Categoria não encontrada.");

    public static readonly Error ProductNotFound =
        Error.NotFound("Catalog.ProductNotFound", "Produto não encontrado.");

    public static readonly Error ParentCategoryNotFound =
        Error.NotFound("Catalog.ParentCategoryNotFound", "A categoria mãe escolhida não existe.");

    public static readonly Error CategoryNameInUse =
        Error.Conflict("Catalog.CategoryNameInUse", "Já existe uma categoria com este nome.");

    public static readonly Error SlugInUse =
        Error.Conflict("Catalog.SlugInUse", "Este endereço já é usado (ou já foi usado) por outra página da loja.");

    /// <summary>RF-050: categoria com produtos ou subcategorias não é excluída (409); desative.</summary>
    public static readonly Error CategoryInUse = Error.Conflict(
        "Catalog.CategoryInUse", "A categoria tem produtos ou subcategorias. Desative em vez de excluir.");

    public static readonly Error ColorNotFound = Error.NotFound("Catalog.ColorNotFound", "Acabamento ou cor não encontrado.");

    public static readonly Error SizeNotFound = Error.NotFound("Catalog.SizeNotFound", "Tamanho não encontrado.");

    public static readonly Error ColorNameInUse = Error.Conflict("Catalog.ColorNameInUse", "Já existe um acabamento ou cor com este nome.");

    public static readonly Error SizeNameInUse = Error.Conflict("Catalog.SizeNameInUse", "Já existe um tamanho com este nome.");

    public static readonly Error ColorInUse = Error.Conflict("Catalog.ColorInUse", "Este acabamento ou cor está em uso em produtos ou fotos.");

    public static readonly Error SizeInUse = Error.Conflict("Catalog.SizeInUse", "Este tamanho está em uso em produtos.");

    public static readonly Error BarcodeNotFound = Error.NotFound(
        "Catalog.BarcodeNotFound", "Nenhuma variação tem este código de barras. Vincule o código a uma variação.");

    public static readonly Error CollectionNotFound = Error.NotFound("Catalog.CollectionNotFound", "Coleção não encontrada.");

    public static readonly Error CollectionNameInUse = Error.Conflict("Catalog.CollectionNameInUse", "Já existe uma coleção com este nome.");

    public static readonly Error OccasionNotFound = Error.NotFound("Catalog.OccasionNotFound", "Ocasião não encontrada.");

    /// <summary>RF-025 CA2.</summary>
    public static readonly Error OccasionOverlap = Error.Conflict(
        "Catalog.OccasionOverlap", "Já existe uma ocasião ativa neste período. Mude as datas ou desative a outra.");

    public static readonly Error ColorNotInProduct = Error.Validation(
        "Catalog.ColorNotInProduct", "Esta peça não tem o acabamento escolhido.");

    public static readonly Error ImageNotFound = Error.NotFound("Catalog.ImageNotFound", "Foto não encontrada.");

    public static readonly Error ImageTypeNotAccepted = Error.Validation(
        "Catalog.ImageTypeNotAccepted", "Formato não aceito. Envie JPEG, PNG, WebP ou HEIC.");

    public static readonly Error ImageUnreadable = Error.Validation(
        "Catalog.ImageUnreadable", "Não foi possível ler esta foto: o arquivo parece corrompido ou é grande demais.");

    /// <summary>RF-051 CA3: a peça é a protagonista da loja (arquivo 26).</summary>
    public static readonly Error ActiveProductNeedsImage = Error.BusinessRule(
        "Catalog.ActiveProductNeedsImage", "Um produto na loja precisa de ao menos uma foto.");

    public static readonly Error ImageOrderOutdated = Error.Conflict(
        "Catalog.ImageOrderOutdated", "As fotos mudaram em outra aba. Recarregue a página e tente de novo.");

    /// <summary>Concorrência otimista (arquivo 06 §6.1): outra pessoa salvou antes.</summary>
    public static readonly Error ConcurrentChange = Error.Conflict(
        "Catalog.ConcurrentChange", "Este cadastro foi alterado por outra pessoa. Recarregue a página e tente de novo.");

    /// <summary>Produto existe mas saiu da loja (inativo ou sem variação ativa); a API responde 410 (SEO-02).</summary>
    public static readonly Error ProductUnavailable =
        Error.NotFound("Catalog.ProductUnavailable", "Este produto não está mais disponível.");
}
