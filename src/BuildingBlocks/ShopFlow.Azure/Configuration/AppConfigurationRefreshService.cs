using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ShopFlow.Azure.Configuration;

/// <summary>
/// Periodic configuration refresh in processes that do not serve HTTP requests.
/// </summary>
/// <remarks>
/// AZURE: the App Configuration provider does not refresh itself. In ASP.NET Core a middleware does it
/// as requests come in, but a queue consumer or a function with a Service Bus trigger has no HTTP
/// requests, so without this service a feature flag change would never reach it.
/// <c>TryRefreshAsync</c> is cheap: it checks the sentinel only after the refresh interval has passed.
/// </remarks>
internal sealed partial class AppConfigurationRefreshService(
    IConfigurationRefresherProvider refresherProvider,
    TimeProvider timeProvider,
    ILogger<AppConfigurationRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval, timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            foreach (var refresher in refresherProvider.Refreshers)
            {
                // A refresh failure must not stop the application: the last valid configuration stays in place.
                if (!await refresher.TryRefreshAsync(stoppingToken).ConfigureAwait(false))
                {
                    LogRefreshFailed(refresher.AppConfigurationEndpoint?.Host);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "App Configuration refresh from {Endpoint} failed; keeping the last known configuration")]
    private partial void LogRefreshFailed(string? endpoint);
}
