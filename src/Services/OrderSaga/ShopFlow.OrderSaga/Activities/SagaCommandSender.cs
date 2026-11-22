using System.Text.Json;
using ShopFlow.Contracts;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Outbox;
using ShopFlow.OrderSaga.Model;

namespace ShopFlow.OrderSaga.Activities;

/// <summary>
/// Logic of the SendSagaCommand activity: sends the command straight to Service Bus, without an outbox.
/// </summary>
/// <remarks>
/// WHY: the saga has no database of its own in which a state change and sending a command would have to be atomic.
/// The orchestration history plays the outbox role: until the activity succeeds, Durable Functions keeps retrying it,
/// and it records the result in the history only after the send. A duplicate from a retry is rejected by duplicate
/// detection (deterministic MessageId) and, after its window, by the receiver's inbox.
/// </remarks>
public sealed class SagaCommandSender(
    MessageTypeRegistry registry,
    MessageEnvelopeFactory envelopes,
    IMessagePublisher publisher)
{
    public Task SendAsync(OutgoingCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!registry.TryResolve(command.ContractName, command.ContractVersion, out var type))
        {
            throw new InvalidOperationException($"Unknown contract {command.ContractName}.v{command.ContractVersion}.");
        }

        var message = (IMessage)JsonSerializer.Deserialize(command.PayloadJson, type, ContractJson.Options)!;
        var envelope = envelopes.Create(message, new MessageMetadata(command.CorrelationId, command.CausationId), command.MessageId);

        return publisher.PublishAsync([new OutboundMessage(envelope, command.Destination)], cancellationToken);
    }
}
