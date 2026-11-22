using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ShopFlow.Messaging;

namespace ShopFlow.CustomerReadModels.Application;

public sealed record CustomerOrderSummary(Guid OrderId, string OrderNumber, string Status, DateTimeOffset SubmittedAt, MoneyView Total, int ItemCount);

/// <param name="ContinuationToken">Opaque marker of the next page, or <c>null</c> when this is the last one.</param>
public sealed record CustomerOrderPage(IReadOnlyList<CustomerOrderSummary> Items, string? ContinuationToken);

public interface ICustomerOrderStore
{
    /// <summary>Point read in the customer's partition.</summary>
    Task<CustomerOrderView?> GetAsync(Guid customerId, Guid orderId, CancellationToken cancellationToken);

    /// <summary>
    /// Saves the view: creates a new one when <see cref="CustomerOrderView.ETag"/> is empty, or overwrites it conditionally
    /// (If-Match). Sets the new ETag after saving.
    /// </summary>
    /// <exception cref="SharedKernel.Persistence.ConcurrencyConflictException">Someone else saved the document in the meantime.</exception>
    Task SaveAsync(CustomerOrderView view, CancellationToken cancellationToken);

    /// <summary>The customer's orders, newest first, without documents that lack a header.</summary>
    Task<CustomerOrderPage> ListAsync(Guid customerId, int pageSize, string? continuationToken, CancellationToken cancellationToken);
}

/// <summary>
/// Cache of order details. The implementation does not throw: an unavailable cache means a database read,
/// not an API or projection error.
/// </summary>
public interface IOrderDetailsCache
{
    Task<CustomerOrderView?> GetAsync(Guid orderId, CancellationToken cancellationToken);

    Task SetAsync(CustomerOrderView view, CancellationToken cancellationToken);

    Task InvalidateAsync(Guid orderId, CancellationToken cancellationToken);
}

/// <summary>
/// Applies an event to the view: read, change, conditional write, cache invalidation.
/// </summary>
/// <remarks>
/// WHY: the readmodels-inbox queue has sessions by <c>orderId</c>, so the events of one order are not projected in parallel
/// and read-modify-write does not race with itself. The conditional write (ETag) stays anyway, because a DLQ replay or a
/// projection rebuild, which do not respect sessions, may run alongside the live traffic.
/// </remarks>
public sealed partial class CustomerOrderProjector(
    ICustomerOrderStore store,
    IOrderDetailsCache cache,
    TimeProvider timeProvider,
    ILogger<CustomerOrderProjector> logger)
{
    public async Task ProjectAsync(
        Guid customerId,
        Guid orderId,
        MessageContext context,
        Action<CustomerOrderView> apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(apply);

        var view = await store.GetAsync(customerId, orderId, cancellationToken).ConfigureAwait(false)
            ?? CustomerOrderView.Placeholder(customerId, orderId);

        if (view.HasApplied(context.MessageId))
        {
            LogDuplicate(context.MessageType, orderId);
            return;
        }

        var now = timeProvider.GetUtcNow();
        apply(view);
        view.MarkApplied(context.MessageId, context.OccurredAt, now);

        await store.SaveAsync(view, cancellationToken).ConfigureAwait(false);
        ReadModelTelemetry.RecordProjectionLag(context.MessageType, now - context.OccurredAt);

        // WHY: invalidate after the write, not before. The reverse order leaves a window in which the API loads the old version from
        // the database into the cache and serves it until the TTL expires.
        await cache.InvalidateAsync(orderId, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{MessageType} for order {OrderId} already projected")]
    private partial void LogDuplicate(string messageType, Guid orderId);
}

/// <summary>Result of an order details read by the API.</summary>
/// <param name="ProjectionPending">
/// The order is not visible yet, but it was created moments ago, so the projection will most likely show it shortly.
/// </param>
public sealed record OrderLookup(CustomerOrderView? View, bool ProjectionPending)
{
    public static OrderLookup Found(CustomerOrderView view) => new(view, false);

    public static readonly OrderLookup NotFound = new(null, false);

    public static readonly OrderLookup Pending = new(null, true);
}

public sealed class CustomerOrderQueries(ICustomerOrderStore store, IOrderDetailsCache cache, TimeProvider timeProvider)
{
    /// <summary>How long after an order is placed a missing view is treated as "not arrived yet" rather than "does not exist".</summary>
    public static readonly TimeSpan ProjectionGracePeriod = TimeSpan.FromMinutes(2);

    public async Task<OrderLookup> GetAsync(Guid customerId, Guid orderId, CancellationToken cancellationToken)
    {
        var cached = await cache.GetAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            // WHY: the Cosmos read is authorized by the partition key (we look only in the signed-in customer's partition), but the
            // cache key is just the orderId. Without this check a customer who knows someone else's order identifier would get its
            // details if they happened to be in the cache.
            return cached.CustomerId == customerId.ToString("D") ? OrderLookup.Found(cached) : OrderLookup.NotFound;
        }

        var view = await store.GetAsync(customerId, orderId, cancellationToken).ConfigureAwait(false);
        if (view is { HasHeader: true })
        {
            await cache.SetAsync(view, cancellationToken).ConfigureAwait(false);
            return OrderLookup.Found(view);
        }

        return IsRecent(orderId) ? OrderLookup.Pending : OrderLookup.NotFound;
    }

    public Task<CustomerOrderPage> ListAsync(Guid customerId, int pageSize, string? continuationToken, CancellationToken cancellationToken) =>
        store.ListAsync(customerId, Math.Clamp(pageSize, 1, 50), continuationToken, cancellationToken);

    /// <remarks>
    /// WHY: order identifiers are UUID v7, which carry the creation time in their first 48 bits. Without an extra query to
    /// Orders we know whether the order was created moments ago (the projection has not caught up) or long ago (the identifier
    /// is wrong or belongs to someone else). This is a simple way to get "read your writes" in CQRS: right after placing an
    /// order the customer gets a "try again in a moment" hint instead of a misleading "does not exist".
    /// </remarks>
    private bool IsRecent(Guid orderId) =>
        UuidV7.TryGetTimestamp(orderId, out var createdAt)
        && timeProvider.GetUtcNow() - createdAt is var age
        && age >= TimeSpan.FromSeconds(-30)
        && age <= ProjectionGracePeriod;
}

public static class UuidV7
{
    /// <summary>Reads the creation time from a version 7 UUID (RFC 9562).</summary>
    public static bool TryGetTimestamp(Guid id, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (id.Version != 7)
        {
            return false;
        }

        // Byte order as in the text form (big-endian): the first 6 bytes are milliseconds since the Unix epoch.
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);

        long milliseconds = 0;
        for (var index = 0; index < 6; index++)
        {
            milliseconds = (milliseconds << 8) | bytes[index];
        }

        timestamp = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return true;
    }
}

/// <summary>Read side metrics.</summary>
/// <remarks>
/// AZURE: <c>shopflow.readmodels.projection_lag</c> goes to Application Insights (<c>customMetrics</c>). An alert on the 95th
/// percentile above a few dozen seconds says that customers see stale orders before they start calling. The lag is measured
/// from the event's occurrence time, so it covers the whole chain: outbox, topic, forward, session.
/// </remarks>
public static class ReadModelTelemetry
{
    public const string MeterName = "ShopFlow.ReadModels";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Histogram<double> ProjectionLag =
        Meter.CreateHistogram<double>("shopflow.readmodels.projection_lag", "s", "Delay between an event occurring and its projection being stored.");

    public static void RecordProjectionLag(string messageType, TimeSpan lag) =>
        ProjectionLag.Record(Math.Max(0, lag.TotalSeconds), new KeyValuePair<string, object?>("shopflow.message.type", messageType));
}

public static class CustomerReadModelsApplicationExtensions
{
    public static IServiceCollection AddCustomerReadModelsApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<CustomerOrderProjector>();
        services.TryAddScoped<CustomerOrderQueries>();

        return services;
    }
}
