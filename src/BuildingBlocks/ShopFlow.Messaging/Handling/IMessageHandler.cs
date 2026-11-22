using ShopFlow.Contracts;

namespace ShopFlow.Messaging.Handling;

/// <summary>
/// Handles one message type in one contract version.
/// </summary>
/// <remarks>
/// Contract with the infrastructure:
/// <list type="bullet">
/// <item>Returning without an exception means success and completes the message.</item>
/// <item>The handler must be idempotent: the same message can arrive more than once.</item>
/// <item>A transient exception causes redelivery; a permanent one moves the message to the DLQ
/// (see <see cref="Failures.MessageFailureClassifier"/>).</item>
/// </list>
/// </remarks>
public interface IMessageHandler<in TMessage>
    where TMessage : IMessage
{
    Task HandleAsync(TMessage message, MessageContext context, CancellationToken cancellationToken);
}

/// <summary>Outcome of an attempt to handle a message. Each of them ends with the message being completed in the broker.</summary>
public enum DispatchOutcome
{
    /// <summary>The handler ran successfully.</summary>
    Handled = 0,

    /// <summary>The consumer has no handler for this type and its policy says to skip such messages.</summary>
    Ignored = 1,

    /// <summary>The inbox detected that the message was already processed.</summary>
    Duplicate = 2,
}
