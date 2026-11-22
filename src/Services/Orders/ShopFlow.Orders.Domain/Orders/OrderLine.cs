using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Orders.Domain.Orders;

/// <summary>
/// Order line with the price and name frozen at the moment the order was placed.
/// </summary>
/// <remarks>
/// WHY: a copy of the name and price, not a reference to the catalog. The catalog belongs to Inventory and
/// changes independently; an order must show what the customer bought even when the product is renamed or
/// withdrawn. This is typical denormalization at a bounded context boundary.
/// </remarks>
public sealed record OrderLine
{
    public const int MaxQuantity = 99;

    public OrderLine(string sku, string productName, int quantity, Money unitPrice)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            throw new DomainException("order_line.sku_required", "SKU is required.");
        }

        if (quantity is < 1 or > MaxQuantity)
        {
            throw new DomainException("order_line.invalid_quantity", $"Quantity must be between 1 and {MaxQuantity}.");
        }

        if (unitPrice.IsNegative)
        {
            throw new DomainException("order_line.negative_price", "Unit price cannot be negative.");
        }

        Sku = sku;
        ProductName = productName;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    public string Sku { get; }

    public string ProductName { get; }

    public int Quantity { get; }

    public Money UnitPrice { get; }

    public Money LineTotal => UnitPrice * Quantity;
}
