using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopFlow.Contracts;
using ShopFlow.Messaging.Diagnostics;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.Inbox;

namespace ShopFlow.Messaging.Handling;

/// <summary>
/// Turns an envelope into a handler call: resolves the type, deserializes, creates a DI scope,
/// sets the context and measures the duration.
/// </summary>
/// <remarks>
/// WHY: the dispatcher neither completes nor rejects the message in the broker. It returns an outcome or
/// throws, and the broker adapter decides how to settle the message. The same logic therefore works
/// under an Azure Functions trigger, under <c>ServiceBusProcessor</c> in a container and in a unit test.
/// </remarks>
public sealed partial class MessageDispatcher(
    IServiceScopeFactory scopeFactory,
    MessageTypeRegistry registry,
    IOptions<MessageHandlerOptions> handlerOptions,
    ILogger<MessageDispatcher> logger)
{
    private readonly MessageHandlerOptions _handlers = handlerOptions.Value;

    public Task<DispatchOutcome> DispatchAsync(
        MessageEnvelope envelope,
        ConsumerEndpoint endpoint,
        DeliveryInfo delivery,
        CancellationToken cancellationToken) =>
        DispatchAsync(envelope, endpoint, delivery, configureScope: null, cancellationToken);

    /// <param name="envelope">The message.</param>
    /// <param name="endpoint">The consumer.</param>
    /// <param name="delivery">Information about the delivery attempt.</param>
    /// <param name="configureScope">
    /// Called on the new DI scope before the handler. Lets the caller pass objects that do not come
    /// from DI to the handler, e.g. the Durable Functions client from the <c>[DurableClient]</c> binding.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<DispatchOutcome> DispatchAsync(
        MessageEnvelope envelope,
        ConsumerEndpoint endpoint,
        DeliveryInfo delivery,
        Action<IServiceProvider>? configureScope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(endpoint);

        using var activity = MessagingTelemetry.StartProcess(envelope, endpoint.Name, delivery.DeliveryCount);
        var startedAt = Stopwatch.GetTimestamp();
        var outcomeTag = "failed";

        try
        {
            var outcome = await DispatchCoreAsync(envelope, endpoint, delivery, configureScope, cancellationToken).ConfigureAwait(false);
            outcomeTag = outcome switch
            {
                DispatchOutcome.Handled => "handled",
                DispatchOutcome.Ignored => "ignored",
                _ => "duplicate",
            };
            return outcome;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            throw;
        }
        finally
        {
            MessagingTelemetry.RecordProcessed(
                envelope.MessageType,
                endpoint.Name,
                outcomeTag,
                Stopwatch.GetElapsedTime(startedAt));
        }
    }

    private async Task<DispatchOutcome> DispatchCoreAsync(
        MessageEnvelope envelope,
        ConsumerEndpoint endpoint,
        DeliveryInfo delivery,
        Action<IServiceProvider>? configureScope,
        CancellationToken cancellationToken)
    {
        if (!registry.TryResolve(envelope.MessageType, envelope.ContractVersion, out var messageType)
            || !_handlers.TryGetInvoker(messageType, out var invoker))
        {
            return HandleUnhandled(envelope, endpoint);
        }

        var message = Deserialize(envelope, messageType);
        var context = MessageContext.From(envelope, endpoint, delivery);

        // One DI scope per message: its own DbContext, its own outbox buffer, its own context.
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            scope.ServiceProvider.GetRequiredService<MessageContextAccessor>().SetIncoming(context);
            configureScope?.Invoke(scope.ServiceProvider);

            try
            {
                await invoker(scope.ServiceProvider, message, context, cancellationToken).ConfigureAwait(false);
                return DispatchOutcome.Handled;
            }
            catch (DuplicateMessageException)
            {
                // The state change from the first processing is already saved together with the inbox entry.
                // The message must be completed, otherwise it would keep coming back.
                LogDuplicate(envelope.MessageId, envelope.MessageType, endpoint.Name, delivery.DeliveryCount);
                return DispatchOutcome.Duplicate;
            }
        }
    }

    private DispatchOutcome HandleUnhandled(MessageEnvelope envelope, ConsumerEndpoint endpoint)
    {
        if (endpoint.UnhandledPolicy == UnhandledMessagePolicy.Ignore)
        {
            LogIgnored(envelope.MessageId, envelope.ContractId.ToString(), endpoint.Name);
            return DispatchOutcome.Ignored;
        }

        throw new PermanentMessageException(
            DeadLetterReasons.UnhandledMessageType,
            $"Consumer '{endpoint.Name}' has no handler for contract {envelope.ContractId}.");
    }

    private static IMessage Deserialize(MessageEnvelope envelope, Type messageType)
    {
        try
        {
            return (IMessage?)JsonSerializer.Deserialize(envelope.Payload.Span, messageType, ContractJson.Options)
                ?? throw new JsonException("Message body is JSON null.");
        }
        catch (JsonException exception)
        {
            // A broken payload will not fix itself on redelivery. Dead-letter immediately, with a reason.
            throw new PermanentMessageException(
                DeadLetterReasons.DeserializationFailed,
                $"Body of {envelope.ContractId} does not match the contract: {exception.Message}",
                exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Duplicate message {MessageId} ({MessageType}) skipped by {Consumer} on delivery {DeliveryCount}")]
    private partial void LogDuplicate(Guid messageId, string messageType, string consumer, int deliveryCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Message {MessageId} ({Contract}) has no handler in {Consumer} and was ignored")]
    private partial void LogIgnored(Guid messageId, string contract, string consumer);
}
