using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ShopFlow.Azure.Redis;

public static class RedisServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="RedisConnection"/> and an <c>IDistributedCache</c> running on the same connection.
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Configuration with a <c>Redis</c> section.</param>
    /// <param name="keyPrefix">
    /// The service's key prefix, e.g. <c>orders:</c>.
    /// </param>
    /// <remarks>
    /// WHY: one Redis instance is shared by several services, and isolation relies on key prefixes. This is
    /// a deliberate cost trade-off (a separate instance per service is a separate bill), described in
    /// docs/adr/0016. The prefix is added automatically, so service code cannot accidentally overwrite another
    /// service's key.
    /// </remarks>
    public static IServiceCollection AddShopFlowRedis(this IServiceCollection services, IConfiguration configuration, string keyPrefix)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);

        services.AddOptions<RedisOptions>().Bind(configuration.GetSection(RedisOptions.SectionName));
        services.TryAddSingleton<RedisConnection>();

        services.AddStackExchangeRedisCache(_ => { });
        services.AddOptions<RedisCacheOptions>().Configure<RedisConnection>((options, connection) =>
        {
            options.InstanceName = keyPrefix;
            options.ConnectionMultiplexerFactory = connection.GetAsync;
        });

        return services;
    }
}
