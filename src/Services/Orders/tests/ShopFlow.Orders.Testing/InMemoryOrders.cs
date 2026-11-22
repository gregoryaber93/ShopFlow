using System.Collections.Concurrent;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Inbox;
using ShopFlow.Messaging.Outbox;
using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.Orders.Domain.Carts;
using ShopFlow.Orders.Domain.Orders;
using ShopFlow.SharedKernel.Persistence;

namespace ShopFlow.Orders.Testing;

/// <summary>
/// "Database" state shared by all DI scopes in a test (singleton).
/// </summary>
public sealed class InMemoryOrderStore
{
    private int _version;

    public ConcurrentDictionary<Guid, (OrderState State, string ETag)> Orders { get; } = new();

    public ConcurrentDictionary<Guid, (Cart Cart, string ETag)> Carts { get; } = new();

    public ConcurrentQueue<OutboundMessage> Published { get; } = new();

    public ConcurrentDictionary<InboxEntry, bool> Inbox { get; } = new();

    public bool FailCartDeletion { get; set; }

    public string NextETag() => $"\"v{Interlocked.Increment(ref _version)}\"";

    public IReadOnlyList<string> PublishedTypes() =>
        [.. Published.Select(message => $"{message.Envelope.MessageType}.v{message.Envelope.ContractVersion}")];
}

/// <summary>
/// In-memory repository with the same guarantees as CosmosOrderRepository: conditional write by ETag and an atomic
/// "save order + drain outbox + save inbox".
/// </summary>
public sealed class InMemoryOrderRepository(
    InMemoryOrderStore store,
    IPendingOutbox outbox,
    IMessageContextAccessor context) : IOrderRepository
{
    public Task<Order?> GetAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(store.Orders.TryGetValue(orderId, out var entry)
            ? Order.Restore(entry.State with { ConcurrencyToken = entry.ETag })
            : null);

    public Task<string> AddAsync(Order order, CancellationToken cancellationToken)
    {
        var etag = store.NextETag();
        if (!store.Orders.TryAdd(order.Id, (order.ToState(), etag)))
        {
            throw new InvalidOperationException($"Order {order.Id} already exists.");
        }

        return Task.FromResult(Commit(order, etag));
    }

    public Task<string> UpdateAsync(Order order, CancellationToken cancellationToken)
    {
        if (!store.Orders.TryGetValue(order.Id, out var current) || current.ETag != order.ConcurrencyToken)
        {
            throw new ConcurrencyConflictException(nameof(Order), order.Id.ToString());
        }

        var etag = store.NextETag();
        if (!store.Orders.TryUpdate(order.Id, (order.ToState(), etag), current))
        {
            throw new ConcurrencyConflictException(nameof(Order), order.Id.ToString());
        }

        return Task.FromResult(Commit(order, etag));
    }

    private string Commit(Order order, string etag)
    {
        if (context.Current is { } incoming && !store.Inbox.TryAdd(incoming.InboxEntry, true))
        {
            throw new DuplicateMessageException(incoming.InboxEntry);
        }

        foreach (var message in outbox.Drain())
        {
            store.Published.Enqueue(message);
        }

        order.SetConcurrencyToken(etag);
        return etag;
    }
}

public sealed class InMemoryCartRepository(InMemoryOrderStore store) : ICartRepository
{
    public Task<Cart?> GetAsync(Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult(store.Carts.TryGetValue(customerId, out var entry)
            ? Cart.Restore(customerId, entry.Cart.Items, entry.Cart.UpdatedAt, entry.ETag)
            : null);

    public Task SaveAsync(Cart cart, CancellationToken cancellationToken)
    {
        var etag = store.NextETag();
        var snapshot = Cart.Restore(cart.CustomerId, cart.Items, cart.UpdatedAt, etag);

        var saved = cart.ConcurrencyToken is null
            ? store.Carts.TryAdd(cart.CustomerId, (snapshot, etag))
            : store.Carts.TryGetValue(cart.CustomerId, out var current)
              && current.ETag == cart.ConcurrencyToken
              && store.Carts.TryUpdate(cart.CustomerId, (snapshot, etag), current);

        if (!saved)
        {
            throw new ConcurrencyConflictException(nameof(Cart), cart.CustomerId.ToString());
        }

        cart.SetConcurrencyToken(etag);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Cart cart, CancellationToken cancellationToken)
    {
        if (store.FailCartDeletion)
        {
            throw new TimeoutException("Simulated Cosmos DB timeout.");
        }

        store.Carts.TryRemove(cart.CustomerId, out _);
        return Task.CompletedTask;
    }
}

public sealed class FakeCatalogPricing : ICatalogPricing
{
    public Dictionary<string, CatalogPrice> Prices { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Unavailable { get; set; }

    public FakeCatalogPricing WithPrice(string sku, decimal amount, bool available = true)
    {
        Prices[sku] = new CatalogPrice(sku, $"Product {sku}", new SharedKernel.Domain.Money(amount, "PLN"), available);
        return this;
    }

    public Task<IReadOnlyDictionary<string, CatalogPrice>> GetPricesAsync(IReadOnlyCollection<string> skus, CancellationToken cancellationToken)
    {
        if (Unavailable)
        {
            throw new CatalogUnavailableException("Simulated catalog outage.");
        }

        IReadOnlyDictionary<string, CatalogPrice> result = skus
            .Where(Prices.ContainsKey)
            .ToDictionary(sku => sku, sku => Prices[sku], StringComparer.OrdinalIgnoreCase);

        return Task.FromResult(result);
    }
}

public sealed class FakeOrderFeatures : IOrderFeatures
{
    public bool PublishV1 { get; set; } = true;

    public bool PickupPoint { get; set; }

    public Task<bool> PublishOrderSubmittedV1Async() => Task.FromResult(PublishV1);

    public Task<bool> PickupPointDeliveryAsync() => Task.FromResult(PickupPoint);
}

public sealed class SequentialOrderNumbers : IOrderNumberGenerator
{
    private int _next;

    public string Next(DateTimeOffset now) => $"SF-{now:yyMMdd}-{Interlocked.Increment(ref _next):D8}";
}
