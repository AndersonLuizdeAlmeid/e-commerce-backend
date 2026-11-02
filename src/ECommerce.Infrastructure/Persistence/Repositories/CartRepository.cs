using Dapper;
using ECommerce.Application.Abstractions.Data;
using ECommerce.Domain.Carts;
using ECommerce.Domain.Catalog;
using ECommerce.Domain.Identity;

namespace ECommerce.Infrastructure.Persistence.Repositories;

internal sealed class CartRepository(IUnitOfWork unitOfWork) : ICartRepository
{
    public async Task<Cart> GetOrCreateForUpdateAsync(UserId customerId, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await unitOfWork.Connection.ExecuteAsync(Command(
            """
            INSERT INTO carts.carts (id, customer_id, updated_at)
            VALUES (@Id, @CustomerId, @Now)
            ON CONFLICT (customer_id) DO NOTHING
            """,
            new { Id = Guid.CreateVersion7(), CustomerId = customerId.Value, Now = nowUtc },
            cancellationToken));

        var cart = await unitOfWork.Connection.QuerySingleAsync<CartRow>(Command(
            "SELECT id, updated_at FROM carts.carts WHERE customer_id = @CustomerId FOR UPDATE",
            new { CustomerId = customerId.Value },
            cancellationToken));

        var items = await unitOfWork.Connection.QueryAsync<ItemRow>(Command(
            "SELECT variant_id, quantity, added_at FROM carts.cart_items WHERE cart_id = @Id ORDER BY added_at, variant_id",
            new { cart.Id },
            cancellationToken));

        return Cart.Rehydrate(
            cart.Id,
            customerId,
            items.Select(item => new CartItem(new VariantId(item.VariantId), item.Quantity, item.AddedAt)),
            cart.UpdatedAt);
    }

    public async Task SaveAsync(Cart cart, CancellationToken cancellationToken)
    {
        await unitOfWork.Connection.ExecuteAsync(Command(
            """
            UPDATE carts.carts SET updated_at = @UpdatedAt WHERE id = @Id;
            DELETE FROM carts.cart_items WHERE cart_id = @Id;
            """,
            new { cart.Id, UpdatedAt = cart.UpdatedAtUtc },
            cancellationToken));

        if (cart.Items.Count == 0)
        {
            return;
        }

        await unitOfWork.Connection.ExecuteAsync(Command(
            "INSERT INTO carts.cart_items (cart_id, variant_id, quantity, added_at) VALUES (@CartId, @VariantId, @Quantity, @AddedAt)",
            cart.Items.Select(item => new { CartId = cart.Id, VariantId = item.VariantId.Value, item.Quantity, AddedAt = item.AddedAtUtc }).ToList(),
            cancellationToken));
    }

    public async Task<IReadOnlySet<VariantId>> FindPurchasableAsync(IReadOnlyCollection<VariantId> variantIds, CancellationToken cancellationToken)
    {
        var ids = await unitOfWork.Connection.QueryAsync<Guid>(Command(
            """
            SELECT v.id
              FROM catalog.product_variants v
              JOIN catalog.products p ON p.id = v.product_id
              JOIN catalog.categories c ON c.id = p.category_id
             WHERE v.id = ANY(@Ids) AND v.is_active AND p.is_active AND c.is_active
            """,
            new { Ids = variantIds.Select(id => id.Value).ToArray() },
            cancellationToken));

        return ids.Select(id => new VariantId(id)).ToHashSet();
    }

    private CommandDefinition Command(string sql, object parameters, CancellationToken cancellationToken) =>
        new(sql, parameters, unitOfWork.Transaction, cancellationToken: cancellationToken);

    private sealed class CartRow
    {
        public Guid Id { get; init; }
        public DateTime UpdatedAt { get; init; }
    }

    private sealed class ItemRow
    {
        public Guid VariantId { get; init; }
        public int Quantity { get; init; }
        public DateTime AddedAt { get; init; }
    }
}
