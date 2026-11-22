using Microsoft.Extensions.Options;
using ShopFlow.Contracts;

namespace ShopFlow.Messaging.Routing;

/// <summary>Answers the question: which queue or topic should a message of a given type go to.</summary>
public interface IMessageRouter
{
    string GetDestination(Type messageType);
}

/// <summary>
/// Route table populated at service startup.
/// </summary>
/// <remarks>
/// WHY: a route is the sender's knowledge, not the contract's. The same event type could go to a different
/// topic in another environment, and the contract is shared with consumers who do not care about that.
/// </remarks>
public sealed class MessageRoutingOptions
{
    internal Dictionary<Type, string> Routes { get; } = [];

    public MessageRoutingOptions Map<TMessage>(string destination)
        where TMessage : IMessage => Map(typeof(TMessage), destination);

    public MessageRoutingOptions Map(Type messageType, string destination)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        if (!typeof(IMessage).IsAssignableFrom(messageType))
        {
            throw new ArgumentException($"{messageType.FullName} does not implement IMessage.", nameof(messageType));
        }

        Routes[messageType] = destination;
        return this;
    }
}

internal sealed class MessageRouter(IOptions<MessageRoutingOptions> options) : IMessageRouter
{
    private readonly Dictionary<Type, string> _routes = options.Value.Routes;

    public string GetDestination(Type messageType) =>
        _routes.TryGetValue(messageType, out var destination)
            ? destination
            : throw new InvalidOperationException(
                $"No route is configured for message type {messageType.FullName}. " +
                "Register it with MessagingBuilder.Route<TMessage>(destination).");
}
