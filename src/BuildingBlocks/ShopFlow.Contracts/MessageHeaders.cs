namespace ShopFlow.Contracts;

/// <summary>
/// Names of message application properties. They are as much a part of the contract as the payload.
/// </summary>
/// <remarks>
/// AZURE: a Service Bus message has broker properties (MessageId, Subject, SessionId, CorrelationId)
/// and an ApplicationProperties dictionary. Subscription filters see only these two places, never the
/// body. Everything we want to route on must live here, not in the JSON.
/// </remarks>
public static class MessageHeaders
{
    /// <summary>
    /// Contract version stored as text (<c>"1"</c>, <c>"2"</c>).
    /// </summary>
    /// <remarks>
    /// AZURE: a correlation filter defined in ARM/Bicep compares properties as strings only. If the
    /// version were a number, the filter <c>ContractVersion = '2'</c> would never match and we would
    /// need the slower SQL filter instead.
    /// </remarks>
    public const string ContractVersion = "ContractVersion";

    /// <summary>Identifier of the message whose handling caused this one to be sent.</summary>
    public const string CausationId = "CausationId";

    /// <summary>Name of the publishing service, e.g. <c>orders</c>.</summary>
    public const string Source = "Source";

    /// <summary>When the business fact happened (not when the message was sent).</summary>
    public const string OccurredAt = "OccurredAt";

    /// <summary>Set by the tool that replays messages from the DLQ.</summary>
    public const string ReplayedFromDeadLetter = "ReplayedFromDeadLetter";

    /// <summary>How many times the message has been replayed from the DLQ.</summary>
    public const string ReplayCount = "ReplayCount";
}
