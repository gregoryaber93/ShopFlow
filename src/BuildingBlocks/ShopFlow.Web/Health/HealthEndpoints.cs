using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using ShopFlow.Observability;

namespace ShopFlow.Web.Health;

public static class HealthEndpoints
{
    /// <summary>
    /// Three health endpoints with different purposes.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>/health/live</c>: the process responds. Liveness probe in Container Apps; failure = container restart.</item>
    /// <item><c>/health/ready</c>: the instance can take traffic. Readiness probe in Container Apps and the App Service
    /// health check; failure = the instance drops out of the load balancer.</item>
    /// <item><c>/health/dependencies</c>: state of dependencies. For diagnostics and availability tests, not for probes.</item>
    /// </list>
    /// AZURE: the App Service health check calls the configured path every minute and after a few failures replaces
    /// the instance with a new one. If <c>/health/ready</c> checked Cosmos DB, a Cosmos outage would mark all instances
    /// unhealthy at once and App Service would start replacing them (up to half of the pool), which fixes nothing and
    /// adds cold starts to the outage. That is why checks of shared dependencies are separate.
    /// </remarks>
    public static IEndpointRouteBuilder MapShopFlowHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
            .AllowAnonymous();

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(HealthTags.Ready),
        }).AllowAnonymous();

        // The result contains dependency names, so it is not public. APIM does not expose /health/* externally.
        endpoints.MapHealthChecks("/health/dependencies", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(HealthTags.Dependencies),
        }).RequireAuthorization();

        return endpoints;
    }
}
