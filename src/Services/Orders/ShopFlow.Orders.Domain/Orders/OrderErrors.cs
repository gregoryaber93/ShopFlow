using ShopFlow.SharedKernel.Results;

namespace ShopFlow.Orders.Domain.Orders;

/// <summary>Expected refusals of order operations. The codes are stable and reach API clients.</summary>
public static class OrderErrors
{
    public static readonly Error NoLines = Error.Validation("order.no_lines", "An order must contain at least one line.");

    public static readonly Error TooManyLines = Error.Validation("order.too_many_lines", $"An order can contain at most {Order.MaxLines} lines.");

    public static readonly Error DuplicateSku = Error.Validation("order.duplicate_sku", "Each SKU may appear only once in an order.");

    public static readonly Error MixedCurrencies = Error.Validation("order.mixed_currencies", "All lines of an order must use the same currency.");

    public static readonly Error NotFound = Error.NotFound("order.not_found", "Order was not found.");

    public static readonly Error CannotCancel = Error.Conflict("order.cannot_cancel", "The order can no longer be cancelled.");

    public static readonly Error CancellationPending = Error.Conflict("order.cancellation_pending", "The customer requested cancellation; the order cannot be confirmed.");

    public static readonly Error AlreadyClosed = Error.Conflict("order.closed", "The order is already closed.");

    /// <summary>A shipping event arrived before the order was confirmed (reordered messages).</summary>
    public static readonly Error NotConfirmedYet = Error.Conflict("order.not_confirmed_yet", "The order has not been confirmed yet.");

    public static readonly Error InvalidTransition = Error.Conflict("order.invalid_transition", "The operation is not allowed in the current order status.");
}
