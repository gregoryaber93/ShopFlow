namespace ShopFlow.Messaging.Failures;

/// <summary>
/// An error that redelivering the message will not fix. The message goes to the DLQ immediately,
/// without using up the delivery limit.
/// </summary>
/// <remarks>
/// A handler throws this exception when it knows the problem lies in the message itself, e.g. a command
/// refers to an order that does not exist.
/// </remarks>
public sealed class PermanentMessageException : Exception
{
    public PermanentMessageException(string reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public PermanentMessageException(string reason, string message, Exception innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }

    /// <summary>Short, stable code from <see cref="DeadLetterReasons"/>. Alerts and KQL queries group by it.</summary>
    public string Reason { get; }
}

/// <summary>
/// Values of the <c>DeadLetterReason</c> property.
/// </summary>
/// <remarks>
/// AZURE: the broker moves a message to the DLQ on its own in two cases and writes its own reason:
/// <c>MaxDeliveryCountExceeded</c> (attempts exhausted) and <c>TTLExpiredException</c> (expired).
/// All other reasons are set by the application. The DLQ runbook starts diagnosis by grouping on this field.
/// </remarks>
public static class DeadLetterReasons
{
    public const string MalformedEnvelope = "MalformedEnvelope";
    public const string DeserializationFailed = "DeserializationFailed";
    public const string UnhandledMessageType = "UnhandledMessageType";
    public const string ValidationFailed = "ValidationFailed";
    public const string BusinessRuleViolation = "BusinessRuleViolation";
    public const string MessageTooLarge = "MessageTooLarge";

    /// <summary>Set by Service Bus, not by the application.</summary>
    public const string MaxDeliveryCountExceeded = "MaxDeliveryCountExceeded";
}
