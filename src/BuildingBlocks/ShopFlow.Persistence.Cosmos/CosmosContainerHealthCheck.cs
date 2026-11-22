using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ShopFlow.Persistence.Cosmos;

/// <summary>
/// Checks that the container is reachable from this instance and that the identity has access to it.
/// </summary>
/// <remarks>
/// AZURE: reading container properties is a metadata operation. The "Cosmos DB Built-in Data Contributor"
/// data-plane role covers it (the readMetadata action), so the check works without management permissions.
/// It catches three typical deployment errors: a missing private endpoint or DNS record (timeout), a
/// missing role assignment (403) and a typo in the container name (404).
/// Registered with the "dependencies" tag, not "ready" (see the comment in HealthEndpoints).
/// </remarks>
public sealed class CosmosContainerHealthCheck(Container container) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await container.ReadContainerAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy(data: new Dictionary<string, object>
            {
                ["container"] = container.Id,
                ["requestCharge"] = response.RequestCharge,
            });
        }
        catch (CosmosException exception)
        {
            return HealthCheckResult.Unhealthy($"Cosmos container '{container.Id}' returned {(int)exception.StatusCode}.", exception);
        }
    }
}
