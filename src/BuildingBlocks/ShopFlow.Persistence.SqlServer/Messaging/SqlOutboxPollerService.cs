using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ShopFlow.Persistence.SqlServer.Messaging;

/// <summary>Background outbox publishing loop running in every replica of the service.</summary>
public sealed partial class SqlOutboxPollerService<TContext>(
    IServiceScopeFactory scopeFactory,
    IOutboxSignal signal,
    IOptions<SqlOutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<SqlOutboxPollerService<TContext>> logger) : BackgroundService
    where TContext : MessagingDbContext
{
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var nextPurgeAt = timeProvider.GetUtcNow();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var scope = scopeFactory.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    var relay = scope.ServiceProvider.GetRequiredService<SqlOutboxRelay<TContext>>();
                    var published = await relay.PublishPendingAsync(stoppingToken).ConfigureAwait(false);

                    if (timeProvider.GetUtcNow() >= nextPurgeAt)
                    {
                        await relay.PurgeAsync(stoppingToken).ConfigureAwait(false);
                        nextPurgeAt = timeProvider.GetUtcNow() + PurgeInterval;
                    }

                    // Full batch: the backlog may be larger, so start the next run right away.
                    if (published == settings.BatchSize)
                    {
                        continue;
                    }
                }

                await signal.WaitAsync(settings.PollingInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // E.g. the database is briefly unavailable. The poller must not die, because with it
                // all of the service's events would stop going out.
                LogPollerError(exception);
                await Task.Delay(ErrorDelay, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Outbox poller iteration failed")]
    private partial void LogPollerError(Exception exception);
}
