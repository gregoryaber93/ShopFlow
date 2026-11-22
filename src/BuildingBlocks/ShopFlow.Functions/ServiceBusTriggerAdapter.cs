using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using ShopFlow.Messaging;
using ShopFlow.Messaging.ServiceBus;

namespace ShopFlow.Functions;

/// <summary>
/// Bridge between the Azure Functions Service Bus trigger and the shared processing policy.
/// </summary>
/// <remarks>
/// Typical use in a function:
/// <code>
/// [Function("HandleInventoryCommands")]
/// public Task Run(
///     [ServiceBusTrigger("inventory-commands", Connection = "ServiceBus", AutoCompleteMessages = false)]
///     ServiceBusReceivedMessage message,
///     ServiceBusMessageActions actions,
///     CancellationToken cancellationToken)
///     => adapter.ProcessAsync(message, actions, Endpoint, cancellationToken);
/// </code>
/// AZURE: <c>AutoCompleteMessages = false</c> is mandatory. With the default (true) the host completes the
/// message after a successful invocation and abandons it after an exception, so a message cannot deliberately be
/// dead-lettered with its own reason: every failure would use up another attempt.
/// The <see cref="ServiceBusMessageActions"/> parameter is an SDK-type binding of the isolated worker model: the
/// worker asks the host over gRPC to settle the message the host keeps locked.
/// </remarks>
public sealed class ServiceBusTriggerAdapter(ServiceBusMessageProcessor processor)
{
    public Task ProcessAsync(
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        ConsumerEndpoint endpoint,
        CancellationToken cancellationToken) =>
        ProcessAsync(message, actions, endpoint, configureScope: null, cancellationToken);

    /// <param name="message">Message from the trigger.</param>
    /// <param name="actions">Message settlement.</param>
    /// <param name="endpoint">The consumer.</param>
    /// <param name="configureScope">
    /// Passes the function's input bindings into the handler's DI scope.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// AZURE: in the isolated worker model some objects exist only as input bindings of function parameters, not as
    /// services in the DI container. Example: the Durable Functions client (<c>[DurableClient]</c>), configured by the
    /// host from host.json. The message handler runs in its own DI scope, so such an object must be passed to it
    /// explicitly.
    /// </remarks>
    public async Task ProcessAsync(
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        ConsumerEndpoint endpoint,
        Action<IServiceProvider>? configureScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(actions);

        try
        {
            await processor
                .ProcessAsync(message, new FunctionsSettlement(message, actions), endpoint, configureScope, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // AZURE: with AutoCompleteMessages = false the host does not settle the message after an exception either.
            // Without an explicit Abandon the message would return only after the lock expires (the queue's lock
            // duration, 1 minute here). ServiceBusMessageProcessor has already waited out the delay before the retry.
            await actions.AbandonMessageAsync(message, cancellationToken: CancellationToken.None).ConfigureAwait(false);

            // Rethrowing marks the invocation as failed in metrics and in Application Insights.
            throw;
        }
    }

    private sealed class FunctionsSettlement(ServiceBusReceivedMessage message, ServiceBusMessageActions actions) : IMessageSettlement
    {
        public Task CompleteAsync(CancellationToken cancellationToken) =>
            actions.CompleteMessageAsync(message, cancellationToken);

        public Task DeadLetterAsync(string reason, string description, CancellationToken cancellationToken) =>
            actions.DeadLetterMessageAsync(
                message,
                deadLetterReason: reason,
                deadLetterErrorDescription: description,
                cancellationToken: cancellationToken);
    }
}
