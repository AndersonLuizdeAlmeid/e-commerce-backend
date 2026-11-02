using ECommerce.Domain.Abstractions;

namespace ECommerce.Domain.Catalog;

public static class CatalogDomainErrors
{
    public static readonly Error InvalidSlug = Error.Validation(
        "Catalog.InvalidSlug", "Endereço inválido: use letras minúsculas, números e hífen (até 120 caracteres).");

    public static readonly Error InvalidName = Error.Validation("Catalog.InvalidName", "Informe um nome de 2 a 120 caracteres.");

    public static readonly Error InvalidOccasionText =
        Error.Validation("Catalog.InvalidOccasionText", "O texto da ocasião tem até 160 caracteres.");

    public static readonly Error InvalidOccasionPeriod =
        Error.Validation("Catalog.InvalidOccasionPeriod", "O fim da ocasião não pode ser antes do início.");

    public static readonly Error InvalidImageAlt =
        Error.Validation("Catalog.InvalidImageAlt", "Descreva a foto em até 200 caracteres.");

    public static readonly Error InvalidHex =
        Error.Validation("Catalog.InvalidHex", "Informe a cor da amostra no formato #RRGGBB.");

    public static readonly Error InvalidVariantLabel =
        Error.Validation("Catalog.InvalidVariantLabel", "Os rótulos da grade têm até 30 caracteres.");

    public static readonly Error InvalidPrice = Error.Validation("Catalog.InvalidPrice", "O preço precisa ser maior que zero.");

    public static readonly Error InvalidPackage =
        Error.Validation("Catalog.InvalidPackage", "Informe peso (g) e medidas (cm) maiores que zero para o frete.");

    public static readonly Error InvalidSku = Error.Validation("Catalog.InvalidSku", "Informe um SKU de até 60 caracteres.");

    public static readonly Error InvalidBarcode = Error.Validation(
        "Catalog.InvalidBarcode", "Código de barras inválido: confira os números (EAN-8, EAN-13 ou GTIN-14).");

    public static readonly Error DuplicateSku = Error.Conflict("Catalog.DuplicateSku", "Já existe uma variação com este SKU.");

    public static readonly Error DuplicateBarcode =
        Error.Conflict("Catalog.DuplicateBarcode", "Este código de barras já está em outra variação.");

    public static readonly Error VariantNotFound = Error.NotFound("Catalog.VariantNotFound", "Variação não encontrada.");

    public static readonly Error ActiveProductNeedsVariant = Error.BusinessRule(
        "Catalog.ActiveProductNeedsVariant", "Um produto na loja precisa de ao menos uma variação ativa.");

    public static readonly Error SubcategoryDepth = Error.BusinessRule(
        "Catalog.SubcategoryDepth", "Só existe um nível de subcategoria: escolha uma categoria principal como mãe.");

    public static readonly Error CategoryCannotBeOwnParent =
        Error.BusinessRule("Catalog.CategoryCannotBeOwnParent", "A categoria não pode ser mãe dela mesma.");
}
