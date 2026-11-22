using System.Globalization;
using System.Text.Json;
using ShopFlow.CustomerReadModels.Application;
using ShopFlow.SharedKernel.Persistence;

namespace ShopFlow.CustomerReadModels.Testing;

/// <summary>
/// In-memory view store with the Cosmos DB semantics that matter to the projection: a copy on read and write (like
/// serialization), an ETag changed on every write, a conflict on a stale ETag.
/// </summary>
public sealed class InMemoryCustomerOrderStore : ICustomerOrderStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<(string CustomerId, string OrderId), CustomerOrderView> _documents = [];
    private int _version;

    public int Writes { get; private set; }

    public IReadOnlyCollection<CustomerOrderView> All => [.. _documents.Values.Select(Clone)];

    public Task<CustomerOrderView?> GetAsync(Guid customerId, Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(_documents.TryGetValue((customerId.ToString("D"), orderId.ToString("D")), out var view) ? Clone(view) : null);

    public Task SaveAsync(CustomerOrderView view, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);

        var key = (view.CustomerId, view.Id);
        var exists = _documents.TryGetValue(key, out var current);
        if ((view.ETag is null && exists) || (view.ETag is not null && current?.ETag != view.ETag))
        {
            throw new ConcurrencyConflictException("CustomerOrderView", view.Id);
        }

        view.ETag = string.Create(CultureInfo.InvariantCulture, $"\"v{++_version}\"");
        _documents[key] = Clone(view);
        Writes++;
        return Task.CompletedTask;
    }

    public Task<CustomerOrderPage> ListAsync(Guid customerId, int pageSize, string? continuationToken, CancellationToken cancellationToken)
    {
        var skip = continuationToken is null ? 0 : int.Parse(continuationToken, CultureInfo.InvariantCulture);
        var visible = _documents.Values
            .Where(view => view.CustomerId == customerId.ToString("D") && view.HasHeader)
            .OrderByDescending(view => view.SubmittedAt)
            .ToList();

        var items = visible.Skip(skip).Take(pageSize)
            .Select(view => new CustomerOrderSummary(view.OrderId, view.OrderNumber!, view.Status, view.SubmittedAt!.Value, view.Total!, view.Lines.Count))
            .ToList();

        var next = skip + items.Count < visible.Count ? (skip + items.Count).ToString(CultureInfo.InvariantCulture) : null;
        return Task.FromResult(new CustomerOrderPage(items, next));
    }

    /// <summary>Inserts a ready-made view, bypassing the projection (test data for the API).</summary>
    public void Seed(CustomerOrderView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        view.ETag ??= string.Create(CultureInfo.InvariantCulture, $"\"v{++_version}\"");
        _documents[(view.CustomerId, view.Id)] = Clone(view);
    }

    private static CustomerOrderView Clone(CustomerOrderView view) =>
        JsonSerializer.Deserialize<CustomerOrderView>(JsonSerializer.SerializeToUtf8Bytes(view, Json), Json)!;
}

public sealed class InMemoryOrderDetailsCache : IOrderDetailsCache
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Dictionary<Guid, byte[]> Entries { get; } = [];

    public List<Guid> Invalidations { get; } = [];

    public Task<CustomerOrderView?> GetAsync(Guid orderId, CancellationToken cancellationToken) =>
        Task.FromResult(Entries.TryGetValue(orderId, out var bytes) ? JsonSerializer.Deserialize<CustomerOrderView>(bytes, Json) : null);

    public Task SetAsync(CustomerOrderView view, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);
        Entries[view.OrderId] = JsonSerializer.SerializeToUtf8Bytes(view, Json);
        return Task.CompletedTask;
    }

    public Task InvalidateAsync(Guid orderId, CancellationToken cancellationToken)
    {
        Entries.Remove(orderId);
        Invalidations.Add(orderId);
        return Task.CompletedTask;
    }
}
