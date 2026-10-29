namespace ECommerce.Application.Catalog.ListProducts;

/// <summary>Whitelist de ordenação (arquivo 06 §4): nada vindo do request é concatenado no SQL.</summary>
internal static class ProductSort
{
    private static readonly Dictionary<string, string> OrderBy = new(StringComparer.Ordinal)
    {
        ["createdAt:desc"] = "created_at DESC",
        ["createdAt:asc"] = "created_at ASC",
        ["price:asc"] = "price_from ASC",
        ["price:desc"] = "price_from DESC",
        ["name:asc"] = "name ASC",
        ["name:desc"] = "name DESC",
    };

    public static IEnumerable<string> Allowed => OrderBy.Keys;

    public static bool IsValid(string sort) => OrderBy.ContainsKey(sort);

    /// <summary>Id como desempate para a paginação ser estável.</summary>
    public static string ToSql(string sort) => OrderBy[sort] + ", id";
}
