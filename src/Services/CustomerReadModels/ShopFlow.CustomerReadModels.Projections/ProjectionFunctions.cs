using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using ShopFlow.Contracts;
using ShopFlow.Functions;
using ShopFlow.Messaging;

namespace ShopFlow.CustomerReadModels.Projections;

public sealed class CustomerOrderProjectionFunction(ServiceBusTriggerAdapter adapter)
{
    /// <remarks>
    /// WHY: the Ignore policy. The <c>readmodels</c> subscriptions have no filters (they get everything from three topics), so
    /// events without a handler are normal here: <c>OrderSubmitted</c> v1 during the sunset period, <c>ShipmentCreationFailed</c>,
    /// and future events the customer screen does not show yet.
    /// </remarks>
    private static readonly ConsumerEndpoint Endpoint = new(MessagingTopology.Consumers.ReadModels, UnhandledMessagePolicy.Ignore);

    /// <remarks>
    /// AZURE: a session-enabled queue, with <c>SessionId</c> = <c>orderId</c> (the publisher sets it for every
    /// <c>IOrderScoped</c> event). The function holds the session lock, so the events of one order are projected one after
    /// another, while different orders run in parallel (up to <c>maxConcurrentSessions</c> per instance).
    /// AZURE: sessions give ordering within the queue, but they do not restore ordering across topics. An event from
    /// payments-events may be forwarded to the queue before an earlier event from orders-events. That is why the view stores
    /// facts rather than "the last status" anyway (see <c>CustomerOrderView</c>).
    /// AZURE: auto-forwarding to a session-enabled queue requires every message to have a <c>SessionId</c>. A message without
    /// one is not forwarded and lands in the subscription's DLQ (reason in <c>DeadLetterReason</c>), not in this queue.
    /// </remarks>
    [Function("customer-orders-projection")]
    public Task Run(
        [ServiceBusTrigger(MessagingTopology.Queues.ReadModelsInbox, Connection = "ServiceBus", IsSessionsEnabled = true, AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken cancellationToken) =>
        adapter.ProcessAsync(message, actions, Endpoint, cancellationToken);
}
