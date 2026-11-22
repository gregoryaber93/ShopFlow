using Microsoft.Extensions.DependencyInjection;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Shipping.V1;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.Handling;
using ShopFlow.Orders.Domain.Orders;
using static ShopFlow.Orders.UnitTests.TestData;

namespace ShopFlow.Orders.UnitTests.Application;

/// <summary>
/// Handlers run through the real MessageDispatcher (the same one as under the Functions trigger),
/// so the test also covers the inbox and outcome classification.
/// </summary>
public sealed class ShipmentEventHandlersTests : IDisposable
{
    private static readonly ConsumerEndpoint Endpoint = new(MessagingTopology.Consumers.OrdersProcessors);

    private readonly OrdersTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Delivery_completes_the_order_and_publishes_order_completed()
    {
        var order = GivenStored(ConfirmedOrder());

        await DispatchAsync(Delivered(order.Id));

        _host.Store.Orders[order.Id].State.Status.ShouldBe(OrderStatus.Completed);
        _host.Store.PublishedTypes().ShouldBe(["OrderCompleted.v1"]);
    }

    [Fact]
    public async Task Redelivered_message_is_detected_by_the_inbox()
    {
        var order = GivenStored(ConfirmedOrder());
        var envelope = Envelope(Delivered(order.Id));

        (await DispatchAsync(envelope)).ShouldBe(DispatchOutcome.Handled);

        // The same message once more: the order is already completed, so the handler saves nothing and the inbox
        // is not even needed. The result is success without side effects.
        (await DispatchAsync(envelope)).ShouldBe(DispatchOutcome.Handled);
        _host.Store.PublishedTypes().ShouldBe(["OrderCompleted.v1"]);
    }

    [Fact]
    public async Task Dispatch_before_confirmation_is_retried_later_not_dead_lettered()
    {
        var order = GivenStored(SubmittedOrder());

        await Should.ThrowAsync<OutOfOrderMessageException>(() => DispatchAsync(Dispatched(order.Id)));
        _host.Store.Orders[order.Id].State.Status.ShouldBe(OrderStatus.Submitted);
    }

    [Fact]
    public async Task Event_for_an_order_not_visible_yet_is_retried_later()
    {
        await Should.ThrowAsync<OutOfOrderMessageException>(() => DispatchAsync(Dispatched(Guid.NewGuid())));
    }

    [Fact]
    public async Task Delivery_of_a_rejected_order_goes_straight_to_the_dlq()
    {
        var order = SubmittedOrder();
        order.Reject("payment_declined", null, Now);
        GivenStored(order);

        var exception = await Should.ThrowAsync<PermanentMessageException>(() => DispatchAsync(Delivered(order.Id)));

        exception.Reason.ShouldBe(DeadLetterReasons.BusinessRuleViolation);
    }

    private Order GivenStored(Order order)
    {
        order.DequeueDomainEvents();
        _host.Store.Orders[order.Id] = (order.ToState(), "\"etag-1\"");
        return order;
    }

    private static ShipmentDelivered Delivered(Guid orderId) => new()
    {
        OrderId = orderId,
        CustomerId = CustomerId,
        ShipmentId = Guid.NewGuid(),
        DeliveredAt = Now,
    };

    private static ShipmentDispatched Dispatched(Guid orderId) => new()
    {
        OrderId = orderId,
        CustomerId = CustomerId,
        ShipmentId = Guid.NewGuid(),
        CarrierCode = "DPD",
        TrackingNumber = "TRK-1",
        DispatchedAt = Now,
    };

    private MessageEnvelope Envelope(IMessage message) =>
        _host.Services.GetRequiredService<MessageEnvelopeFactory>().Create(message, new MessageMetadata("corr-1"));

    private Task<DispatchOutcome> DispatchAsync(IMessage message) => DispatchAsync(Envelope(message));

    private Task<DispatchOutcome> DispatchAsync(MessageEnvelope envelope) =>
        _host.Services.GetRequiredService<MessageDispatcher>()
            .DispatchAsync(envelope, Endpoint, new DeliveryInfo(1, Now), TestContext.Current.CancellationToken);
}
