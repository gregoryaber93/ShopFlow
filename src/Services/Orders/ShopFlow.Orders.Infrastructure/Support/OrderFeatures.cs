using System.Security.Cryptography;
using Microsoft.FeatureManagement;
using ShopFlow.Orders.Application.Abstractions;

namespace ShopFlow.Orders.Infrastructure.Support;

/// <summary>
/// Feature flags from Azure App Configuration.
/// </summary>
/// <remarks>
/// AZURE: a flag is the <c>.appconfig.featureflag/Orders.PublishOrderSubmittedV1</c> key in App Configuration
/// (definitions in infra/modules/config/app-configuration.bicep). The configuration provider refreshes flags every
/// 30 seconds, so a change in the portal takes effect on all instances without a deployment or restart.
/// A flag can also have a filter (e.g. a percentage of users or a time window), which allows a feature to be
/// rolled out gradually.
/// </remarks>
internal sealed class OrderFeatures(IFeatureManager featureManager) : IOrderFeatures
{
    public Task<bool> PublishOrderSubmittedV1Async() => featureManager.IsEnabledAsync(OrderFeatureFlags.PublishOrderSubmittedV1);

    public Task<bool> PickupPointDeliveryAsync() => featureManager.IsEnabledAsync(OrderFeatureFlags.PickupPointDelivery);
}

/// <summary>
/// Human-facing order number: <c>SF-261121-7K3QX9MD</c> (date + 8 Crockford Base32 characters).
/// </summary>
/// <remarks>
/// WHY: a random number, not a counter. A counter in Cosmos DB is one document changed with every order: a hot
/// partition limited to the throughput of a single logical partition, and with concurrent orders an avalanche of
/// 412 conflicts and retries. 40 random bits give about a trillion combinations per day, so a collision within one
/// day is practically impossible. Uniqueness is guaranteed by the order identifier anyway; the number is only for
/// people.
/// </remarks>
internal sealed class OrderNumberGenerator : IOrderNumberGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public string Next(DateTimeOffset now)
    {
        Span<byte> random = stackalloc byte[5];
        RandomNumberGenerator.Fill(random);

        var value = ((ulong)random[0] << 32) | ((ulong)random[1] << 24) | ((ulong)random[2] << 16) | ((ulong)random[3] << 8) | random[4];

        Span<char> suffix = stackalloc char[8];
        for (var index = 7; index >= 0; index--)
        {
            suffix[index] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }

        return $"SF-{now:yyMMdd}-{suffix}";
    }
}
