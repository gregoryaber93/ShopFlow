using ShopFlow.Contracts;

namespace ShopFlow.Messaging;

public sealed class MessagingOptions
{
    /// <summary>Service name written to the <see cref="MessageHeaders.Source"/> header.</summary>
    public string ServiceName { get; set; } = "unknown";
}
