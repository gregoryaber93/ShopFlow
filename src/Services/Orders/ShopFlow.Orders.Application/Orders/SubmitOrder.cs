using FluentValidation;
using Microsoft.Extensions.Logging;
using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.Orders.Domain.Orders;
using ShopFlow.SharedKernel.Results;

namespace ShopFlow.Orders.Application.Orders;

public sealed record DeliveryInput(DeliveryMethod Method, ShippingAddress? Address, string? PickupPointId);

/// <summary>Places an order from the current content of the customer's cart.</summary>
public sealed record SubmitOrderCommand(Guid CustomerId, DeliveryInput Delivery, string PaymentMethodToken);

public sealed record SubmittedOrder(Guid OrderId, string OrderNumber, OrderStatus Status, string ETag);

public sealed class SubmitOrderCommandValidator : AbstractValidator<SubmitOrderCommand>
{
    public SubmitOrderCommandValidator()
    {
        RuleFor(command => command.CustomerId).NotEmpty();
        RuleFor(command => command.PaymentMethodToken).NotEmpty().MaximumLength(256);

        RuleFor(command => command.Delivery.Address)
            .NotNull()
            .When(command => command.Delivery.Method == DeliveryMethod.Courier)
            .WithMessage("Courier delivery requires an address.");

        RuleFor(command => command.Delivery.PickupPointId)
            .NotEmpty()
            .When(command => command.Delivery.Method == DeliveryMethod.PickupPoint)
            .WithMessage("Pickup point delivery requires a pickup point.");

        When(command => command.Delivery.Address is not null, () =>
        {
            RuleFor(command => command.Delivery.Address!.RecipientName).NotEmpty().MaximumLength(100);
            RuleFor(command => command.Delivery.Address!.Line1).NotEmpty().MaximumLength(200);
            RuleFor(command => command.Delivery.Address!.City).NotEmpty().MaximumLength(100);
            RuleFor(command => command.Delivery.Address!.PostalCode).NotEmpty().MaximumLength(20);
            RuleFor(command => command.Delivery.Address!.CountryCode).NotEmpty().Length(2);
        });
    }
}

public sealed partial class SubmitOrderHandler(
    ICartRepository carts,
    ICatalogPricing catalog,
    IOrderRepository orders,
    IOrderNumberGenerator orderNumbers,
    IOrderFeatures features,
    OrderEventPublisher publisher,
    TimeProvider timeProvider,
    ILogger<SubmitOrderHandler> logger)
{
    public static readonly Error CartEmpty = Error.Validation("order.cart_empty", "The cart is empty.");

    public static readonly Error PickupPointUnavailable = Error.Validation(
        "order.pickup_point_unavailable",
        "Pickup point delivery is not available yet.");

    public async Task<Result<SubmittedOrder>> HandleAsync(SubmitOrderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Delivery.Method == DeliveryMethod.PickupPoint && !await features.PickupPointDeliveryAsync().ConfigureAwait(false))
        {
            return PickupPointUnavailable;
        }

        var cart = await carts.GetAsync(command.CustomerId, cancellationToken).ConfigureAwait(false);
        if (cart is null || cart.IsEmpty)
        {
            return CartEmpty;
        }

        // The server sets prices from the catalog. The client does not send prices, so there is nothing to "verify"
        // and no way to tamper with them.
        var prices = await catalog.GetPricesAsync([.. cart.Items.Keys], cancellationToken).ConfigureAwait(false);

        string[] unavailable =
        [
            .. cart.Items.Keys.Where(sku => !prices.TryGetValue(sku, out var price) || !price.IsAvailable),
        ];

        if (unavailable.Length > 0)
        {
            return Error.Validation("order.items_unavailable", $"These items are no longer available: {string.Join(", ", unavailable)}.");
        }

        OrderLine[] lines =
        [
            .. cart.Items.Select(item =>
            {
                var price = prices[item.Key];
                return new OrderLine(price.Sku, price.ProductName, item.Value, price.UnitPrice);
            }),
        ];

        var now = timeProvider.GetUtcNow();
        var delivery = command.Delivery.Method == DeliveryMethod.Courier
            ? Delivery.Courier(command.Delivery.Address!)
            : Delivery.PickupPoint(command.Delivery.PickupPointId!);

        var submitted = Order.Submit(
            Guid.CreateVersion7(now),
            command.CustomerId,
            orderNumbers.Next(now),
            lines,
            delivery,
            command.PaymentMethodToken,
            now);

        if (!submitted.TryGetValue(out var order))
        {
            return submitted.Error;
        }

        await publisher.PublishAsync(order).ConfigureAwait(false);
        var etag = await orders.AddAsync(order, cancellationToken).ConfigureAwait(false);

        await ClearCartAsync(cart, order.Id, cancellationToken).ConfigureAwait(false);

        return new SubmittedOrder(order.Id, order.OrderNumber, order.Status, etag);
    }

    /// <summary>
    /// Empties the cart after the order is saved, on a "best effort" basis.
    /// </summary>
    /// <remarks>
    /// AZURE: the cart (container <c>carts</c>, key <c>/customerId</c>) and the order (container <c>orders</c>,
    /// key <c>/orderId</c>) live in different containers and partitions, so they cannot be changed in one
    /// transaction.
    /// WHY: an error here must not end the request with a 5xx. The order already exists, and the idempotency filter
    /// releases the key after a 5xx response. A retry with the same key would run the handler again and create a
    /// second order. A leftover cart is a minor inconvenience for the customer; a duplicate order is a double charge
    /// on the card.
    /// </remarks>
    private async Task ClearCartAsync(Domain.Carts.Cart cart, Guid orderId, CancellationToken cancellationToken)
    {
        try
        {
            await carts.DeleteAsync(cart, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCartNotCleared(exception, cart.CustomerId, orderId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order {OrderId} was submitted but cart of customer {CustomerId} could not be cleared")]
    private partial void LogCartNotCleared(Exception exception, Guid customerId, Guid orderId);
}
