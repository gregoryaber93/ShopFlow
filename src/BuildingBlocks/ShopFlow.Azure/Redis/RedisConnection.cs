using Azure.Core;
using Microsoft.Azure.StackExchangeRedis;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace ShopFlow.Azure.Redis;

public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    /// <summary>
    /// E.g. <c>amr-shopflow-prod-weu.westeurope.redis.azure.net:10000</c>.
    /// </summary>
    /// <remarks>
    /// AZURE: Azure Managed Redis (the successor of Azure Cache for Redis) listens on port 10000 and only
    /// over TLS. With the "Enterprise" clustering policy the client sees a single endpoint and keys are
    /// distributed across shards on the server side. With the "OSS" policy the client must understand the
    /// Redis cluster protocol (StackExchange.Redis does), but it skips one proxy hop.
    /// </remarks>
    public string? Endpoint { get; set; }
}

/// <summary>
/// One shared Redis connection per process.
/// </summary>
/// <remarks>
/// AZURE: <see cref="IConnectionMultiplexer"/> multiplexes all commands over a few TCP connections and
/// is meant to live for the whole lifetime of the process. Creating it per request is a classic mistake
/// that ends with the cache tier's connection limit exhausted and timeouts.
/// <para>
/// Authentication: access keys are disabled on the Managed Redis resource and the service's managed
/// identity is added as a Redis user (access policy assignment in Bicep). The
/// Microsoft.Azure.StackExchangeRedis library obtains an Entra ID token, sends it as the AUTH password and
/// re-authenticates open connections before the token expires.
/// </para>
/// </remarks>
public sealed class RedisConnection(IOptions<RedisOptions> options, TokenCredential credential) : IAsyncDisposable
{
    private readonly Lazy<Task<IConnectionMultiplexer>> _connection = new(
        () => ConnectAsync(options.Value, credential),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public Task<IConnectionMultiplexer> GetAsync() => _connection.Value;

    public async ValueTask DisposeAsync()
    {
        if (_connection.IsValueCreated)
        {
            var multiplexer = await _connection.Value.ConfigureAwait(false);
            await multiplexer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<IConnectionMultiplexer> ConnectAsync(RedisOptions options, TokenCredential credential)
    {
        if (string.IsNullOrWhiteSpace(options.Endpoint))
        {
            throw new InvalidOperationException("Redis:Endpoint is not configured.");
        }

        var configuration = ConfigurationOptions.Parse(options.Endpoint);
        configuration.Ssl = true;

        // WHY: with false, ConnectAsync returns a multiplexer even when Redis is briefly unavailable and
        // connects in the background. A short cache outage at instance startup is therefore not "remembered"
        // in the Lazy as a permanent failure, and calling code treats the cache as optional anyway.
        configuration.AbortOnConnectFail = false;
        configuration.ConnectTimeout = 5000;
        configuration.SyncTimeout = 2000;
        configuration.AsyncTimeout = 2000;

        await configuration.ConfigureForAzureWithTokenCredentialAsync(credential).ConfigureAwait(false);
        return await ConnectionMultiplexer.ConnectAsync(configuration).ConfigureAwait(false);
    }
}
