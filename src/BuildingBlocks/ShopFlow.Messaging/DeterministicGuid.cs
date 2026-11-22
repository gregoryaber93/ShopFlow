using System.Security.Cryptography;
using System.Text;

namespace ShopFlow.Messaging;

/// <summary>
/// Name-based identifier: the same input always produces the same GUID.
/// </summary>
/// <remarks>
/// WHY: at-least-once applies to the sender too. A saga activity that sent a command and crashed before
/// recording the result will run again and send the command a second time. When the message identifier
/// is derived from business data (e.g. <c>{orderId}/ReserveStock/1</c>), both copies have the same
/// MessageId and the broker rejects the second one.
/// </remarks>
public static class DeterministicGuid
{
    /// <summary>ShopFlow identifier namespace (any fixed GUID).</summary>
    public static readonly Guid ShopFlowNamespace = new("6f1c1d0e-7b0a-4f4e-9a52-0d2f5a3c9b11");

    public static Guid Create(string name) => Create(ShopFlowNamespace, name);

    public static Guid Create(Guid namespaceId, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var nameByteCount = Encoding.UTF8.GetByteCount(name);
        var input = new byte[16 + nameByteCount];
        namespaceId.TryWriteBytes(input, bigEndian: true, out _);
        Encoding.UTF8.GetBytes(name, input.AsSpan(16));

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(input, hash);

        // Layout follows RFC 9562, version 8 ("custom"): the UUID v5 algorithm, but with SHA-256.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return new Guid(hash[..16], bigEndian: true);
    }
}
