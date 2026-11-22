using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using ShopFlow.Contracts;

namespace ShopFlow.Messaging.Handling;

/// <summary>Handler invocation for an already deserialized message.</summary>
public delegate Task MessageHandlerInvoker(
    IServiceProvider services,
    IMessage message,
    MessageContext context,
    CancellationToken cancellationToken);

/// <summary>
/// "Message type → how to invoke the handler" map, built once at startup.
/// </summary>
/// <remarks>
/// WHY: a delegate closing over generic types instead of reflection for every message. The consumer's
/// hot path has no <c>MakeGenericType</c> or <c>MethodInfo.Invoke</c>, and the code is trimming-friendly.
/// </remarks>
public sealed class MessageHandlerOptions
{
    private readonly Dictionary<Type, MessageHandlerInvoker> _invokers = [];

    public IReadOnlyCollection<Type> HandledTypes => _invokers.Keys;

    public void Add<TMessage, THandler>()
        where TMessage : class, IMessage
        where THandler : class, IMessageHandler<TMessage>
    {
        if (_invokers.ContainsKey(typeof(TMessage)))
        {
            throw new InvalidOperationException(
                $"A handler for {typeof(TMessage).FullName} is already registered. " +
                "One consumer handles a given message type exactly once.");
        }

        _invokers[typeof(TMessage)] = static (services, message, context, cancellationToken) =>
            services.GetRequiredService<THandler>().HandleAsync((TMessage)message, context, cancellationToken);
    }

    public bool TryGetInvoker(Type messageType, [NotNullWhen(true)] out MessageHandlerInvoker? invoker) =>
        _invokers.TryGetValue(messageType, out invoker);
}
