using ShopFlow.SharedKernel.Domain;
using ShopFlow.SharedKernel.Results;

namespace ShopFlow.Inventory.Domain.Catalog;

public sealed record ProductPriceChangedDomainEvent(string Sku, Money OldPrice, Money NewPrice, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>
/// A product in the catalog. Its identifier is the SKU, the business key used by every context.
/// </summary>
/// <remarks>
/// WHY: unlike Orders, here the domain is mapped by EF Core directly, without a separate document model.
/// EF Core can write and rehydrate private fields, so the entity stays closed (no public setters). The cost is a
/// private parameterless constructor for materialization and a price stored in two fields instead of a
/// <see cref="Money"/>. In Cosmos DB an explicit document with a schema version works better, because the schema
/// lives in the application; in SQL migrations guard the schema.
/// </remarks>
public sealed class Product : AggregateRoot<string>
{
    private decimal _priceAmount;
    private string _priceCurrency = "PLN";

    /// <summary>For materialization by EF Core (sets fields through backing fields).</summary>
    private Product()
        : base(string.Empty)
    {
    }

    private Product(string sku)
        : base(sku)
    {
    }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public int CategoryId { get; private set; }

    public bool IsActive { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Money Price => new(_priceAmount, _priceCurrency);

    public string Sku => Id;

    public static Product Create(string sku, string name, string? description, int categoryId, Money price, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sku);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new Product(sku.ToUpperInvariant())
        {
            Name = name,
            Description = description,
            CategoryId = categoryId,
            _priceAmount = price.Amount,
            _priceCurrency = price.Currency,
            IsActive = true,
            UpdatedAt = now,
        };
    }

    public Result ChangePrice(Money newPrice, DateTimeOffset now)
    {
        if (newPrice.IsNegative || newPrice.IsZero)
        {
            return Error.Validation("product.invalid_price", "Price must be greater than zero.");
        }

        if (newPrice.Currency != _priceCurrency)
        {
            return Error.Validation("product.currency_change", "Changing the currency of a product is not supported.");
        }

        if (newPrice == Price)
        {
            return Result.Success();
        }

        var oldPrice = Price;
        _priceAmount = newPrice.Amount;
        UpdatedAt = now;
        Raise(new ProductPriceChangedDomainEvent(Sku, oldPrice, newPrice, now));
        return Result.Success();
    }

    public void Discontinue(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAt = now;
    }
}

public sealed class Category
{
    private Category()
    {
    }

    public Category(int id, string name, int? parentId)
    {
        Id = id;
        Name = name;
        ParentId = parentId;
    }

    public int Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public int? ParentId { get; private set; }
}
