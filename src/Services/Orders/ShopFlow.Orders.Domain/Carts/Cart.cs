using ShopFlow.SharedKernel.Domain;
using ShopFlow.SharedKernel.Results;

namespace ShopFlow.Orders.Domain.Carts;

/// <summary>
/// The customer's cart. The cart identifier is the customer identifier: one active cart per customer.
/// </summary>
/// <remarks>
/// The cart stores only SKUs and quantities, without prices. The price is set when the order is placed, based on
/// the catalog, so there is no "stale price in the cart" problem.
/// </remarks>
public sealed class Cart : AggregateRoot<Guid>
{
    public const int MaxDistinctItems = 50;
    public const int MaxQuantityPerItem = 99;

    private readonly Dictionary<string, int> _items;

    private Cart(Guid customerId, Dictionary<string, int> items, DateTimeOffset updatedAt)
        : base(customerId)
    {
        _items = items;
        UpdatedAt = updatedAt;
    }

    public Guid CustomerId => Id;

    public IReadOnlyDictionary<string, int> Items => _items;

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool IsEmpty => _items.Count == 0;

    public static Cart Create(Guid customerId, DateTimeOffset now) =>
        new(customerId, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), now);

    public static Cart Restore(Guid customerId, IReadOnlyDictionary<string, int> items, DateTimeOffset updatedAt, string? concurrencyToken)
    {
        var cart = new Cart(customerId, new Dictionary<string, int>(items, StringComparer.OrdinalIgnoreCase), updatedAt);
        cart.SetConcurrencyToken(concurrencyToken);
        return cart;
    }

    /// <summary>Sets the quantity of a SKU. Zero removes the line.</summary>
    public Result SetQuantity(string sku, int quantity, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            return Error.Validation("cart.sku_required", "SKU is required.");
        }

        if (quantity is < 0 or > MaxQuantityPerItem)
        {
            return Error.Validation("cart.invalid_quantity", $"Quantity must be between 0 and {MaxQuantityPerItem}.");
        }

        if (quantity == 0)
        {
            _items.Remove(sku);
        }
        else
        {
            if (!_items.ContainsKey(sku) && _items.Count >= MaxDistinctItems)
            {
                return Error.Validation("cart.too_many_items", $"A cart can hold at most {MaxDistinctItems} distinct items.");
            }

            _items[sku] = quantity;
        }

        UpdatedAt = now;
        return Result.Success();
    }
}
