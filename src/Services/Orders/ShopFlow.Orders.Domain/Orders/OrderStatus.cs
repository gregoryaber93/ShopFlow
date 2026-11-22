namespace ShopFlow.Orders.Domain.Orders;

/// <summary>
/// Order lifecycle.
/// </summary>
/// <remarks>
/// <code>
///                 ┌──────────────► Rejected
///                 │
/// Submitted ──────┼──► Confirmed ──► Shipped ──► Completed
///     │           │        │                        ▲
///     │           │        └────────────────────────┘ (delivered without a prior "dispatched")
///     ▼           │
/// CancellationRequested ──► Cancelled
///                 │
///                 └──► Rejected (the saga failed before it could handle the cancellation)
/// </code>
/// Submitted → Confirmed/Rejected: decided by the saga (orchestration). Confirmed → Shipped → Completed:
/// events from Shipping (choreography).
/// </remarks>
public enum OrderStatus
{
    Submitted = 0,
    CancellationRequested = 1,
    Confirmed = 2,
    Shipped = 3,
    Completed = 4,
    Rejected = 5,
    Cancelled = 6,
}

public static class OrderStatusExtensions
{
    /// <summary>Terminal state: no further transition is possible.</summary>
    public static bool IsClosed(this OrderStatus status) =>
        status is OrderStatus.Completed or OrderStatus.Rejected or OrderStatus.Cancelled;
}
