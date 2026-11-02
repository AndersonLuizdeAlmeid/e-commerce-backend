using ECommerce.Domain.Abstractions;
using ECommerce.Domain.Catalog;
using ECommerce.Domain.Identity;

namespace ECommerce.Domain.Carts;

public static class CartErrors
{
    public static readonly Error InvalidQuantity =
        Error.BusinessRule("Carts.InvalidQuantity", $"A quantidade por peça vai de 1 a {Cart.MaxQuantityPerItem}.");

    public static readonly Error TooManyItems =
        Error.BusinessRule("Carts.TooManyItems", $"A sacola tem até {Cart.MaxItems} peças diferentes.");

    public static readonly Error VariantUnavailable =
        Error.BusinessRule("Carts.VariantUnavailable", "Esta peça não está disponível na loja.");

    public static readonly Error ItemNotInCart = Error.NotFound("Carts.ItemNotInCart", "Esta peça não está na sacola.");
}

public sealed record CartItem(VariantId VariantId, int Quantity, DateTime AddedAtUtc);

/// <summary>Sacola do cliente (arquivo 04 §4.6, RN-040). Guarda só variação e quantidade: preço e saldo vêm sempre da loja (RF-030).</summary>
public sealed class Cart
{
    public const int MaxItems = 50;
    public const int MaxQuantityPerItem = 99;

    private readonly List<CartItem> _items;

    private Cart(Guid id, UserId customerId, List<CartItem> items, DateTime updatedAtUtc)
    {
        Id = id;
        CustomerId = customerId;
        _items = items;
        UpdatedAtUtc = updatedAtUtc;
    }

    public Guid Id { get; }

    public UserId CustomerId { get; }

    public IReadOnlyList<CartItem> Items => _items;

    public DateTime UpdatedAtUtc { get; private set; }

    /// <summary>Uso exclusivo da Infrastructure.</summary>
    public static Cart Rehydrate(Guid id, UserId customerId, IEnumerable<CartItem> items, DateTime updatedAtUtc) =>
        new(id, customerId, items.ToList(), updatedAtUtc);

    /// <summary>Adicionar uma peça que já está na sacola soma à quantidade.</summary>
    public Result Add(VariantId variantId, int quantity, DateTime nowUtc)
    {
        var index = IndexOf(variantId);
        var current = index < 0 ? 0 : _items[index].Quantity;
        if (quantity < 1 || current + quantity > MaxQuantityPerItem)
        {
            return Result.Failure(CartErrors.InvalidQuantity);
        }

        if (index < 0 && _items.Count >= MaxItems)
        {
            return Result.Failure(CartErrors.TooManyItems);
        }

        if (index < 0)
        {
            _items.Add(new CartItem(variantId, quantity, nowUtc));
        }
        else
        {
            _items[index] = _items[index] with { Quantity = current + quantity };
        }

        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }

    public Result SetQuantity(VariantId variantId, int quantity, DateTime nowUtc)
    {
        if (quantity is < 1 or > MaxQuantityPerItem)
        {
            return Result.Failure(CartErrors.InvalidQuantity);
        }

        var index = IndexOf(variantId);
        if (index < 0)
        {
            return Result.Failure(CartErrors.ItemNotInCart);
        }

        _items[index] = _items[index] with { Quantity = quantity };
        UpdatedAtUtc = nowUtc;
        return Result.Success();
    }

    /// <summary>Remover o que já não está na sacola não é erro: o resultado é o mesmo.</summary>
    public void Remove(VariantId variantId, DateTime nowUtc)
    {
        if (_items.RemoveAll(item => item.VariantId == variantId) > 0)
        {
            UpdatedAtUtc = nowUtc;
        }
    }

    public void Clear(DateTime nowUtc)
    {
        _items.Clear();
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>
    /// Traz a sacola do visitante ao entrar (RF-031): soma as quantidades com limite por peça, em vez de recusar, para
    /// o login nunca falhar por causa da sacola. Devolve o que não coube.
    /// </summary>
    public IReadOnlyList<VariantId> Merge(IEnumerable<(VariantId VariantId, int Quantity)> items, DateTime nowUtc)
    {
        var skipped = new List<VariantId>();
        foreach (var (variantId, quantity) in items)
        {
            var index = IndexOf(variantId);
            if (index >= 0)
            {
                _items[index] = _items[index] with { Quantity = Math.Min(_items[index].Quantity + quantity, MaxQuantityPerItem) };
            }
            else if (_items.Count < MaxItems)
            {
                _items.Add(new CartItem(variantId, Math.Min(quantity, MaxQuantityPerItem), nowUtc));
            }
            else
            {
                skipped.Add(variantId);
            }
        }

        UpdatedAtUtc = nowUtc;
        return skipped;
    }

    private int IndexOf(VariantId variantId) => _items.FindIndex(item => item.VariantId == variantId);
}

public interface ICartRepository
{
    /// <summary>Cria a sacola se preciso e trava a linha até o fim da transação: dois toques seguidos não perdem item.</summary>
    Task<Cart> GetOrCreateForUpdateAsync(UserId customerId, DateTime nowUtc, CancellationToken cancellationToken);

    Task SaveAsync(Cart cart, CancellationToken cancellationToken);

    /// <summary>Das variações pedidas, as que estão à venda: variação, produto e categoria ativos (RF-030 CA2).</summary>
    Task<IReadOnlySet<VariantId>> FindPurchasableAsync(IReadOnlyCollection<VariantId> variantIds, CancellationToken cancellationToken);
}
