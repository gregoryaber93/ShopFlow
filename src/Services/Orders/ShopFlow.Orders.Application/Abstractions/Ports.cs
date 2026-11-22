using ShopFlow.Orders.Domain.Carts;
using ShopFlow.Orders.Domain.Orders;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Orders.Application.Abstractions;

/// <summary>
/// Reading and writing orders.
/// </summary>
/// <remarks>
/// Contract for implementations: <see cref="AddAsync"/> and <see cref="UpdateAsync"/> save the order together
/// with the messages collected during the current operation by <c>IOutbox</c> (and with the inbox entry if a
/// message started the operation) in one transaction. That is why the application layer publishes events
/// before calling save, not after.
/// </remarks>
public interface IOrderRepository
{
    Task<Order?> GetAsync(Guid orderId, CancellationToken cancellationToken);

    /// <returns>The ETag of the saved document.</returns>
    Task<string> AddAsync(Order order, CancellationToken cancellationToken);

    /// <summary>Conditional write: only if the version in the database is still <see cref="AggregateRoot{TId}.ConcurrencyToken"/>.</summary>
    /// <returns>The new ETag.</returns>
    /// <exception cref="SharedKernel.Persistence.ConcurrencyConflictException">Someone changed the order in the meantime.</exception>
    Task<string> UpdateAsync(Order order, CancellationToken cancellationToken);
}

public interface ICartRepository
{
    Task<Cart?> GetAsync(Guid customerId, CancellationToken cancellationToken);

    /// <summary>Creates or replaces the cart with version control. Sets the new version token on the cart.</summary>
    /// <exception cref="SharedKernel.Persistence.ConcurrencyConflictException">The cart was changed concurrently.</exception>
    Task SaveAsync(Cart cart, CancellationToken cancellationToken);

    Task DeleteAsync(Cart cart, CancellationToken cancellationToken);
}

/// <summary>Product price and name according to the catalog (Inventory) at the time of the query.</summary>
public sealed record CatalogPrice(string Sku, string ProductName, Money UnitPrice, bool IsAvailable);

/// <summary>Port to the product catalog. The adapter calls inventory-api over HTTP.</summary>
public interface ICatalogPricing
{
    /// <returns>Prices of the SKUs found. A SKU missing from the result means the catalog does not know it.</returns>
    /// <exception cref="CatalogUnavailableException">The catalog does not respond and the cache has no prices.</exception>
    Task<IReadOnlyDictionary<string, CatalogPrice>> GetPricesAsync(IReadOnlyCollection<string> skus, CancellationToken cancellationToken);
}

/// <summary>The catalog is unavailable and prices cannot be determined safely. The API answers 503.</summary>
public sealed class CatalogUnavailableException : Exception
{
    public CatalogUnavailableException(string message)
        : base(message)
    {
    }

    public CatalogUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public interface IOrderNumberGenerator
{
    string Next(DateTimeOffset now);
}

/// <summary>Orders feature flags. Values come from Azure App Configuration.</summary>
public interface IOrderFeatures
{
    /// <summary>Also publish OrderSubmitted v1 (sunset period of the old version).</summary>
    Task<bool> PublishOrderSubmittedV1Async();

    /// <summary>Allow pickup-point delivery (requires v1 publishing to be switched off).</summary>
    Task<bool> PickupPointDeliveryAsync();
}

public static class OrderFeatureFlags
{
    public const string PublishOrderSubmittedV1 = "Orders.PublishOrderSubmittedV1";
    public const string PickupPointDelivery = "Orders.PickupPointDelivery";
}
