using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using ShopFlow.Contracts;
using ShopFlow.Functions;
using ShopFlow.Messaging;
using ShopFlow.Payments.Infrastructure.Cosmos;
using ShopFlow.Persistence.Cosmos;

namespace ShopFlow.Payments.Functions.Functions;

public sealed class PaymentMessagingFunctions(ServiceBusTriggerAdapter adapter, CosmosOutboxRelay relay)
{
    private static readonly ConsumerEndpoint CommandsEndpoint = new(MessagingTopology.Consumers.Payments, UnhandledMessagePolicy.DeadLetter);

    // A separate consumer name: the inbox distinguishes consumers, and invoicing is a different role from command handling.
    private static readonly ConsumerEndpoint InvoicingEndpoint = new("payments-invoicing", UnhandledMessagePolicy.DeadLetter);

    /// <remarks>
    /// AZURE: a session trigger. The host locks the session (all commands of one order) and processes its messages one by
    /// one, while different sessions run in parallel (maxConcurrentSessions in host.json). When handling a command fails
    /// transiently, the delay before the retry holds up the whole session. That is desirable: CapturePayment must not
    /// overtake an AuthorizePayment that is waiting for its retry.
    /// </remarks>
    [Function("payment-commands")]
    public Task Commands(
        [ServiceBusTrigger(MessagingTopology.Queues.PaymentsCommands, Connection = "ServiceBus", IsSessionsEnabled = true, AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken cancellationToken) =>
        adapter.ProcessAsync(message, actions, CommandsEndpoint, cancellationToken);

    [Function("payments-invoicing")]
    public Task Invoicing(
        [ServiceBusTrigger(MessagingTopology.Topics.PaymentsEvents, MessagingTopology.Subscriptions.Invoicing, Connection = "ServiceBus", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken cancellationToken) =>
        adapter.ProcessAsync(message, actions, InvoicingEndpoint, cancellationToken);

    /// <remarks>Unlimited retry policy: see the comment in OrdersOutboxRelayFunction (Orders.Processors).</remarks>
    [Function("payments-outbox-relay")]
    [ExponentialBackoffRetry(-1, "00:00:02", "00:05:00")]
    public Task OutboxRelay(
        [CosmosDBTrigger(
            databaseName: PaymentsContainers.DatabaseName,
            containerName: PaymentsContainers.PaymentsName,
            Connection = "Cosmos",
            LeaseContainerName = PaymentsContainers.LeasesName,
            LeaseContainerPrefix = "outbox-relay/",
            CreateLeaseContainerIfNotExists = false,
            MaxItemsPerInvocation = 100)]
        IReadOnlyList<JsonElement> changes,
        CancellationToken cancellationToken) =>
        relay.RelayAsync(changes, PaymentsContainers.PaymentsName, cancellationToken);
}
