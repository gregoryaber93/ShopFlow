using System.ComponentModel.DataAnnotations;

namespace ShopFlow.Messaging.ServiceBus;

/// <summary>
/// Connection to the Service Bus namespace.
/// </summary>
/// <remarks>
/// AZURE: the section is deliberately called <c>ServiceBus</c> and has a <c>fullyQualifiedNamespace</c> key.
/// The Azure Functions Service Bus trigger, configured with <c>Connection = "ServiceBus"</c>, looks for the
/// <c>ServiceBus__fullyQualifiedNamespace</c> setting and then connects with a managed identity instead of a
/// connection string. Our own client reads the same section, so the namespace address is configured in one
/// place for both triggers and publishing.
/// </remarks>
public sealed class ServiceBusOptions
{
    public const string SectionName = "ServiceBus";

    /// <summary>E.g. <c>sbns-shopflow-prod-weu.servicebus.windows.net</c>. The address only, no key.</summary>
    [Required]
    public string FullyQualifiedNamespace { get; set; } = string.Empty;

    /// <summary>
    /// AMQP over WebSockets (port 443) instead of plain AMQP (port 5671).
    /// </summary>
    /// <remarks>
    /// AZURE: needed only when a firewall on the way allows port 443 only. In our network traffic goes
    /// through a private endpoint in the same VNet, so we keep the faster AMQP over TCP.
    /// </remarks>
    public bool UseWebSockets { get; set; }

    public ServiceBusRetrySettings Retry { get; set; } = new();
}

/// <summary>
/// Retries performed by the SDK within a single operation (send, complete, receive).
/// </summary>
/// <remarks>
/// AZURE: this is a different layer from message redelivery. The SDK retries an individual call on
/// transient errors (ServerBusy, dropped connection, timeout). Only when this layer gives up does the
/// exception reach our code, and the message goes back to the queue.
/// </remarks>
public sealed class ServiceBusRetrySettings
{
    [Range(0, 10)]
    public int MaxRetries { get; set; } = 5;

    public TimeSpan Delay { get; set; } = TimeSpan.FromMilliseconds(800);

    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout of a single attempt. A Premium namespace answers in milliseconds; a minute is headroom for failover.</summary>
    public TimeSpan TryTimeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>Behaviour on processing errors.</summary>
public sealed class ServiceBusProcessingOptions
{
    public const string SectionName = "ServiceBus:Processing";

    /// <summary>Delay before giving a message back after the first transient error.</summary>
    public TimeSpan RedeliveryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Upper bound of the delay. Must be clearly shorter than the queue's lock duration.</summary>
    public TimeSpan RedeliveryMaxDelay { get; set; } = TimeSpan.FromSeconds(30);
}
