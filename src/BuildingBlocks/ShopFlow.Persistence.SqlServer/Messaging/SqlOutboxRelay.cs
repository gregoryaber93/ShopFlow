using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopFlow.Messaging.Diagnostics;
using ShopFlow.Messaging.Outbox;

namespace ShopFlow.Persistence.SqlServer.Messaging;

/// <summary>
/// One publishing run: claim a batch of pending rows, send them, mark them as published.
/// </summary>
/// <remarks>
/// WHY: every container replica has its own poller and all of them read the same table. Claiming rows
/// (<c>ClaimedUntil</c>) ensures two replicas never send the same batch at the same time, and claim expiry
/// ensures a crashed replica's batch does not get stuck forever.
/// <para>
/// Publishing order is preserved within a batch, but not globally: when replica A fails to publish its batch,
/// replica B may send later rows in the meantime. Strict ordering would require a single active publisher
/// (e.g. <c>sp_getapplock</c>) and would limit throughput to one instance. We accept this, because Inventory's
/// consumers (the saga) must tolerate reordered replies anyway, and Service Bus sessions order processing on
/// the receiving side.
/// </para>
/// </remarks>
public sealed partial class SqlOutboxRelay<TContext>(
    TContext dbContext,
    IMessagePublisher publisher,
    IOptions<SqlOutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<SqlOutboxRelay<TContext>> logger)
    where TContext : MessagingDbContext
{
    private readonly SqlOutboxOptions _options = options.Value;
    private readonly string _instanceId = Environment.MachineName;

    /// <summary>Returns the number of published messages. A result equal to the batch size means more may be waiting.</summary>
    public async Task<int> PublishPendingAsync(CancellationToken cancellationToken)
    {
        var claimed = await ClaimBatchAsync(cancellationToken).ConfigureAwait(false);
        if (claimed.Count == 0)
        {
            return 0;
        }

        try
        {
            await publisher
                .PublishAsync([.. claimed.Select(message => message.ToOutboundMessage())], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ReleaseWithErrorAsync(claimed, exception, cancellationToken).ConfigureAwait(false);
            LogPublishFailed(exception, claimed.Count, claimed.Max(message => message.Attempts));
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        long[] sequences = [.. claimed.Select(message => message.Sequence)];

        // If the process crashes between sending and this write, the rows are sent again after the claim
        // expires. The duplicate is removed by the broker's duplicate detection or the receiver's inbox.
        await dbContext.OutboxMessages
            .Where(message => sequences.Contains(message.Sequence))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.PublishedAt, now)
                    .SetProperty(message => message.ClaimedUntil, (DateTimeOffset?)null)
                    .SetProperty(message => message.ClaimedBy, (string?)null),
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var message in claimed)
        {
            MessagingTelemetry.RecordOutboxLag(now - message.CreatedAt, "sql");
        }

        return claimed.Count;
    }

    /// <summary>Deletes old published outbox rows and old inbox entries, in chunks.</summary>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        const int chunk = 1000;
        var now = timeProvider.GetUtcNow();
        var publishedBefore = now - _options.PublishedRetention;
        var processedBefore = now - _options.InboxRetention;

        // WHY: chunks instead of one DELETE. Deleting hundreds of thousands of rows with one statement holds locks
        // and bloats the transaction log, which in Azure SQL shows up as write throttling (log rate limit) for the
        // whole database, including production traffic.
        var outbox = await dbContext.OutboxMessages
            .Where(message => message.PublishedAt != null && message.PublishedAt < publishedBefore)
            .OrderBy(message => message.Sequence)
            .Take(chunk)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        var inbox = await dbContext.InboxMessages
            .Where(message => message.ProcessedAt < processedBefore)
            .OrderBy(message => message.ProcessedAt)
            .Take(chunk)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        return outbox + inbox;
    }

    private async Task<List<OutboxMessage>> ClaimBatchAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var claimedUntil = now + _options.ClaimDuration;

        // AZURE/SQL: the locking hints do all the work here.
        //  - UPDLOCK: rows read in the CTE are immediately locked for update, so two replicas cannot
        //    claim the same row.
        //  - READPAST: rows locked by another replica are skipped instead of blocking the query.
        //    Replicas therefore take successive batches in parallel instead of waiting for each other.
        //  - ROWLOCK: asks the engine not to escalate the lock to a page or the table.
        // The whole thing is one statement, so claiming and reading are atomic without an explicit transaction.
        return await dbContext.OutboxMessages
            .FromSql($"""
                WITH [batch] AS (
                    SELECT TOP ({_options.BatchSize}) *
                    FROM [messaging].[OutboxMessages] WITH (ROWLOCK, UPDLOCK, READPAST)
                    WHERE [PublishedAt] IS NULL
                      AND ([ClaimedUntil] IS NULL OR [ClaimedUntil] < {now})
                      AND [Attempts] < {_options.MaxAttempts}
                    ORDER BY [Sequence]
                )
                UPDATE [batch]
                SET [ClaimedUntil] = {claimedUntil},
                    [ClaimedBy] = {_instanceId},
                    [Attempts] = [Attempts] + 1
                OUTPUT inserted.*
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ReleaseWithErrorAsync(List<OutboxMessage> claimed, Exception exception, CancellationToken cancellationToken)
    {
        // The next attempt no sooner than 2^attempts seconds later (max 5 minutes): during a longer
        // Service Bus outage the poller does not flood the logs or burn through its attempt limit in minutes.
        var attempts = claimed.Max(message => message.Attempts);
        var backoff = TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempts, 9))));
        var retryAt = timeProvider.GetUtcNow() + backoff;
        var error = exception.Message.Length > 2000 ? exception.Message[..2000] : exception.Message;
        long[] sequences = [.. claimed.Select(message => message.Sequence)];

        await dbContext.OutboxMessages
            .Where(message => sequences.Contains(message.Sequence))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(message => message.ClaimedUntil, retryAt)
                    .SetProperty(message => message.LastError, error),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Publishing {Count} outbox message(s) failed (attempt {Attempt}); the batch will be retried")]
    private partial void LogPublishFailed(Exception exception, int count, int attempt);
}
