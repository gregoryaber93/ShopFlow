namespace ShopFlow.Contracts;

/// <summary>
/// Converts an older version of a contract into a newer one.
/// </summary>
/// <remarks>
/// WHY: during the sunset period the publisher sends both versions side by side, so day to day a
/// consumer simply subscribes to the version it understands. An upcaster is needed in one case:
/// when old messages are replayed from the DLQ or an archive after the old version's handler was removed.
/// </remarks>
public interface IMessageUpcaster<in TFrom, out TTo>
    where TFrom : IMessage
    where TTo : IMessage
{
    TTo Upcast(TFrom message);
}
