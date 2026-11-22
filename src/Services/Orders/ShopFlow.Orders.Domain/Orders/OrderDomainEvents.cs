using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Orders.Domain.Orders;

// WHY: every change of order state raises a domain event, including those with no external counterpart
// (OrderShipped). The application layer uses this as a single rule: "there are events = there is something
// to save". An operation repeated on an order already in the target state raises no event and ends without
// a write, i.e. it is idempotent by definition.

public sealed record OrderSubmittedDomainEvent(Guid OrderId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderCancellationRequestedDomainEvent(Guid OrderId, string RequestedBy, string? Reason, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderConfirmedDomainEvent(Guid OrderId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderRejectedDomainEvent(Guid OrderId, string ReasonCode, string? Description, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderCancelledDomainEvent(Guid OrderId, string? Reason, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderShippedDomainEvent(Guid OrderId, string TrackingNumber, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record OrderCompletedDomainEvent(Guid OrderId, DateTimeOffset OccurredAt) : IDomainEvent;
