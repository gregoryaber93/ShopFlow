using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using ShopFlow.Contracts;
using ShopFlow.Functions;
using ShopFlow.Messaging;

namespace ShopFlow.Orders.Processors.Functions;

/// <summary>
/// Choreography after confirmation: Orders reacts to Shipping events (dispatched, delivered).
/// </summary>
public sealed class ShipmentEventsFunction(ServiceBusTriggerAdapter adapter)
{
    // The "orders" subscription has a SQL filter for two event types, so any other message here is a filter
    // misconfiguration. The DeadLetter policy makes such a message land in the DLQ instead of disappearing.
    private static readonly ConsumerEndpoint Endpoint = new(MessagingTopology.Consumers.OrdersProcessors, UnhandledMessagePolicy.DeadLetter);

    /// <remarks>
    /// AZURE: the trigger reads the subscription directly, without an intermediate queue, because Orders listens to
    /// only one topic. The permission is the Azure Service Bus Data Receiver role granted on this single subscription
    /// (scope: .../topics/shipping-events/subscriptions/orders), not on the whole namespace.
    /// </remarks>
    [Function("shipment-events")]
    public Task Run(
        [ServiceBusTrigger(
            MessagingTopology.Topics.ShippingEvents,
            MessagingTopology.Subscriptions.Orders,
            Connection = "ServiceBus",
            AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken cancellationToken) =>
        adapter.ProcessAsync(message, actions, Endpoint, cancellationToken);
}
