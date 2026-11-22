using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace ShopFlow.Contracts;

/// <summary>
/// Two-way map between a contract identifier and a CLR type.
/// </summary>
/// <remarks>
/// WHY: the consumer only gets the <c>Subject</c> string and a version number from the broker. The
/// registry maps them to a type to deserialize without <c>Type.GetType(string)</c>, which would let a
/// sender pick any type from any assembly (the classic deserialization vulnerability).
/// </remarks>
public sealed class MessageTypeRegistry
{
    private readonly FrozenDictionary<MessageContractId, Type> _typesById;
    private readonly FrozenDictionary<Type, MessageContractId> _idsByType;

    private MessageTypeRegistry(Dictionary<MessageContractId, Type> typesById)
    {
        _typesById = typesById.ToFrozenDictionary();
        _idsByType = typesById.ToFrozenDictionary(pair => pair.Value, pair => pair.Key);
    }

    /// <summary>Registry of all contracts defined in this package.</summary>
    public static MessageTypeRegistry Default { get; } = FromAssemblies(typeof(MessageTypeRegistry).Assembly);

    public IReadOnlyCollection<MessageContractId> Contracts => _typesById.Keys;

    public static MessageTypeRegistry FromAssemblies(params Assembly[] assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);

        var typesById = new Dictionary<MessageContractId, Type>();

        foreach (var type in assemblies.SelectMany(assembly => assembly.GetTypes()))
        {
            if (!typeof(IMessage).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
            {
                continue;
            }

            var attribute = type.GetCustomAttribute<MessageContractAttribute>()
                ?? throw new InvalidOperationException(
                    $"Message type {type.FullName} must be annotated with [MessageContract].");

            var id = new MessageContractId(attribute.Name, attribute.Version);

            if (!typesById.TryAdd(id, type))
            {
                throw new InvalidOperationException(
                    $"Contract {id} is declared by both {typesById[id].FullName} and {type.FullName}.");
            }
        }

        return new MessageTypeRegistry(typesById);
    }

    public bool TryResolve(string name, int version, [NotNullWhen(true)] out Type? messageType) =>
        _typesById.TryGetValue(new MessageContractId(name, version), out messageType);

    /// <summary>Whether any version of a contract with this name exists.</summary>
    public bool IsKnownName(string name) => _typesById.Keys.Any(id => id.Name == name);

    public MessageContractId GetContractId(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);

        return _idsByType.TryGetValue(messageType, out var id)
            ? id
            : throw new InvalidOperationException($"Type {messageType.FullName} is not a registered message contract.");
    }

    public MessageContractId GetContractId<TMessage>()
        where TMessage : IMessage => GetContractId(typeof(TMessage));
}
