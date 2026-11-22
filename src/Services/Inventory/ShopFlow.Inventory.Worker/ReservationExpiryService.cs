using Microsoft.Extensions.Options;
using ShopFlow.Inventory.Application.Abstractions;
using ShopFlow.Inventory.Application.Reservations;
using ShopFlow.SharedKernel.Persistence;

namespace ShopFlow.Inventory.Worker;

/// <summary>
/// Safety net: releases reservations that nobody committed or released within their lifetime.
/// </summary>
/// <remarks>
/// WHY: runs in every worker replica, without leader election. Replicas may pick the same reservation, but the second
/// one's write ends with a rowversion conflict, which is ignored here. Leader election (e.g. a blob lease) is an extra
/// moving part that can get stuck. With a few replicas and rare expiries an occasional conflict is cheaper.
/// </remarks>
internal sealed partial class ReservationExpiryService(
    IServiceScopeFactory scopeFactory,
    IOptions<ReservationOptions> options,
    TimeProvider timeProvider,
    ILogger<ReservationExpiryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var timer = new PeriodicTimer(settings.ExpiryInterval, timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await ExpireBatchAsync(settings.ExpiryBatchSize, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogBatchFailed(exception);
            }
        }
    }

    private async Task ExpireBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        IReadOnlyList<Guid> candidates;
        var lookup = scopeFactory.CreateAsyncScope();
        await using (lookup.ConfigureAwait(false))
        {
            candidates = await lookup.ServiceProvider.GetRequiredService<ExpireReservationHandler>()
                .FindExpiredAsync(batchSize, cancellationToken)
                .ConfigureAwait(false);
        }

        var expired = 0;
        foreach (var reservationId in candidates)
        {
            // A separate scope (its own DbContext and transaction) per reservation: a conflict on one
            // does not roll back the release of the others.
            var scope = scopeFactory.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                try
                {
                    if (await scope.ServiceProvider.GetRequiredService<ExpireReservationHandler>().ExpireAsync(reservationId, cancellationToken).ConfigureAwait(false))
                    {
                        expired++;
                    }
                }
                catch (ConcurrencyConflictException)
                {
                    // Another replica (or ReleaseStock from the saga) changed this reservation in the meantime.
                }
            }
        }

        if (expired > 0)
        {
            LogExpired(expired);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Released {Count} expired reservation(s); a saga did not confirm or compensate them in time")]
    private partial void LogExpired(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Reservation expiry batch failed")]
    private partial void LogBatchFailed(Exception exception);
}
