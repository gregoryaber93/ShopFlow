namespace ShopFlow.Contracts;

/// <summary>
/// Gives a message a stable name and version number, independent of the CLR type name.
/// </summary>
/// <remarks>
/// WHY: the name on the wire must not depend on the namespace or class name; otherwise a plain
/// refactoring (moving the type) would be a breaking change for every consumer.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class MessageContractAttribute(string name, int version) : Attribute
{
    /// <summary>Logical name, e.g. <c>OrderSubmitted</c>. Goes into the message's <c>Subject</c> field.</summary>
    public string Name { get; } = name;

    /// <summary>Contract version number. Incremented only for a breaking change.</summary>
    public int Version { get; } = version;
}

/// <summary>The (name, version) pair that uniquely identifies a contract.</summary>
public readonly record struct MessageContractId(string Name, int Version)
{
    public override string ToString() => $"{Name}.v{Version}";
}
