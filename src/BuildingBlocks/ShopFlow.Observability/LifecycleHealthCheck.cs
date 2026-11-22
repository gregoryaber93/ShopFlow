using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace ShopFlow.Observability;

/// <summary>
/// The instance becomes ready only after a full startup and stops being ready when it starts shutting down.
/// </summary>
/// <remarks>
/// AZURE (App Service): during a slot swap the platform warms up the staging slot by calling the path from the
/// <c>WEBSITE_SWAP_WARMUP_PING_PATH</c> setting (<c>/health/ready</c> here) and switches traffic only after a 200.
/// <para>
/// AZURE (Container Apps): the readiness probe removes a replica from the load balancer when it returns an error.
/// After the shutdown signal (SIGTERM on scale-in or a revision change) we answer 503, so new requests go to
/// other replicas and in-flight ones finish within the shutdown grace period.
/// </para>
/// </remarks>
public sealed class LifecycleHealthCheck(IHostApplicationLifetime lifetime) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (lifetime.ApplicationStopping.IsCancellationRequested)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Instance is shutting down."));
        }

        return Task.FromResult(lifetime.ApplicationStarted.IsCancellationRequested
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Instance is still starting."));
    }
}

public static class LifecycleHealthCheckExtensions
{
    public static IHealthChecksBuilder AddLifecycleCheck(this IHealthChecksBuilder builder) =>
        builder.AddCheck<LifecycleHealthCheck>("lifecycle", tags: [HealthTags.Ready]);
}
