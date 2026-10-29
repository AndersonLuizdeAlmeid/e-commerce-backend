namespace ECommerce.Application.Abstractions.Paging;

/// <summary>Envelope de paginação (arquivo 07 §1).</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalItems, int TotalPages);

public static class PagedResponse
{
    public static PagedResponse<T> Create<T>(IReadOnlyList<T> items, int page, int pageSize, int totalItems) =>
        new(items, page, pageSize, totalItems, (int)Math.Ceiling(totalItems / (double)pageSize));
}
