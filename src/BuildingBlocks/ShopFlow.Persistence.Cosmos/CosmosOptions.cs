using System.ComponentModel.DataAnnotations;

namespace ShopFlow.Persistence.Cosmos;

/// <summary>
/// Connection to the Cosmos DB account and the bounded context's database.
/// </summary>
/// <remarks>
/// AZURE: the <c>Cosmos</c> section with the <c>accountEndpoint</c> key is also the configuration of the
/// Cosmos DB trigger in Azure Functions (<c>Connection = "Cosmos"</c> looks for the
/// <c>Cosmos__accountEndpoint</c> setting and connects with a managed identity).
/// </remarks>
public sealed class CosmosOptions
{
    public const string SectionName = "Cosmos";

    /// <summary>E.g. <c>https://cosmos-shopflow-prod-weu.documents.azure.com:443/</c>. The address only, no key.</summary>
    [Required]
    public string AccountEndpoint { get; set; } = string.Empty;

    /// <summary>Database owned by this service. The service identity has permissions on this database only.</summary>
    [Required]
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>
    /// Region the application runs in, e.g. <c>West Europe</c>.
    /// </summary>
    /// <remarks>
    /// AZURE: with a multi-region account the SDK routes reads to the nearest replica. Without this
    /// setting it always picks the write region, which after adding a second region means unnecessary
    /// cross-region traffic.
    /// </remarks>
    public string? ApplicationRegion { get; set; }

    /// <summary>
    /// Gateway mode (HTTPS 443) instead of Direct (TCP).
    /// </summary>
    /// <remarks>
    /// AZURE: Direct connects straight to partition replicas on ports 10000–20000 and has lower latency.
    /// It works through a private endpoint without extra configuration. Gateway is needed only where a
    /// firewall allows port 443 only, and for the emulator in tests.
    /// </remarks>
    public bool UseGatewayMode { get; set; }

    /// <summary>
    /// How many times the SDK itself retries a request rejected with 429 (RU/s exceeded).
    /// </summary>
    /// <remarks>
    /// AZURE: a 429 response carries the <c>x-ms-retry-after-ms</c> header and the SDK waits exactly that long.
    /// Only after these attempts are exhausted does the exception reach the application. That is why single
    /// 429s in the account metrics are normal with autoscale; the problem starts when 429s show up in the
    /// application logs (see the cosmos-throttling runbook).
    /// </remarks>
    [Range(0, 20)]
    public int MaxRetryAttemptsOnRateLimitedRequests { get; set; } = 9;

    public TimeSpan MaxRetryWaitTimeOnRateLimitedRequests { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// After how long Cosmos removes an outbox record on its own.
    /// </summary>
    /// <remarks>
    /// AZURE: per-document TTL works only when TTL is enabled on the container
    /// (<c>defaultTtl = -1</c>, i.e. "enabled, no default expiry"). Deletion runs in the background on
    /// spare RU, so no clean-up job is needed. A TTL deletion does not appear in the Change Feed in
    /// the default mode.
    /// </remarks>
    public TimeSpan OutboxTimeToLive { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// How long processed messages are remembered. Must be longer than the longest possible redelivery
    /// time: the broker's retry window plus how long a message can sit in the DLQ before it is replayed.
    /// </summary>
    public TimeSpan InboxTimeToLive { get; set; } = TimeSpan.FromDays(7);
}
