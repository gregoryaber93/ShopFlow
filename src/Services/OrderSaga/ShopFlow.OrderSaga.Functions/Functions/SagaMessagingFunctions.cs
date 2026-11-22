using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.DependencyInjection;
using ShopFlow.Contracts;
using ShopFlow.Functions;
using ShopFlow.Messaging;
using ShopFlow.OrderSaga.Messaging;

namespace ShopFlow.OrderSaga.Functions.Functions;

/// <summary>Entry into the saga from Service Bus: start (OrderSubmitted) and participant replies.</summary>
public sealed class SagaMessagingFunctions(ServiceBusTriggerAdapter adapter)
{
    private static readonly ConsumerEndpoint Endpoint = new(MessagingTopology.Consumers.OrderSaga, UnhandledMessagePolicy.DeadLetter);

    /// <remarks>
    /// AZURE: the saga-start subscription has the filter <c>sys.Label = 'OrderSubmitted' AND ContractVersion = '2'</c>
    /// (a SQL rule, because this topic adds the <c>ReplayTarget IS NULL</c> condition, see messaging-topology.json).
    /// Without the version filter the saga would get every order twice, because during the sunset period Orders
    /// publishes both v1 and v2. A v1 event that arrives anyway has no handler, so the DeadLetter policy moves it to the
    /// DLQ as a signal of a misconfigured filter.
    /// </remarks>
    [Function("saga-start")]
    public Task Start(
        [ServiceBusTrigger(MessagingTopology.Topics.OrdersEvents, MessagingTopology.Subscriptions.SagaStart, Connection = "ServiceBus", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        [DurableClient] DurableTaskClient client,
        CancellationToken cancellationToken) =>
        adapter.ProcessAsync(message, actions, Endpoint, scope => scope.GetRequiredService<DurableClientHolder>().Client = client, cancellationToken);

    /// <remarks>
    /// AZURE: <c>IsSessionsEnabled = true</c> must match the queue setting (requiresSession in Bicep). A session trigger
    /// locks the whole session (all messages of one order) while it is being handled, so two replies for the same saga
    /// are never raised in parallel.
    /// </remarks>
    [Function("saga-replies")]
    public Task Replies(
        [ServiceBusTrigger(MessagingTopology.Queues.SagaReplies, Connection = "ServiceBus", IsSessionsEnabled = true, AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        [DurableClient] DurableTaskClient client,
        CancellationToken cancellationToken) =>
        adapter.ProcessAsync(message, actions, Endpoint, scope => scope.GetRequiredService<DurableClientHolder>().Client = client, cancellationToken);
}
