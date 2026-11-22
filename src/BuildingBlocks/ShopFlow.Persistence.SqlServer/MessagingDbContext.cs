using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Inbox;
using ShopFlow.Messaging.Outbox;
using ShopFlow.Persistence.SqlServer.Messaging;
using ShopFlow.SharedKernel.Persistence;

namespace ShopFlow.Persistence.SqlServer;

/// <summary>
/// Base DbContext of a service that publishes messages through an outbox in SQL.
/// </summary>
/// <remarks>
/// WHY: the SQL outbox is simpler than the Cosmos one, because SQL has regular transactions.
/// <c>SaveChangesAsync</c> writes entity changes, outbox rows and the inbox entry in one transaction that
/// EF Core opens automatically. Nothing has to be partitioned and there is no operation limit to watch.
/// <para>
/// Dependencies come in through the constructor, not through <c>this.GetService()</c>, to make it explicit
/// that the context lives in the operation's scope. Consequence: DbContext pooling cannot be used, because a
/// pooled context would carry another operation's outbox buffer.
/// </para>
/// </remarks>
public abstract class MessagingDbContext(
    DbContextOptions options,
    IPendingOutbox pendingOutbox,
    IMessageContextAccessor contextAccessor,
    IOutboxSignal outboxSignal,
    TimeProvider timeProvider) : DbContext(options)
{
    // SQL Server error codes: primary key / unique index violation.
    private const int PrimaryKeyViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var inboxEntry = contextAccessor.Current?.InboxEntry;

        if (inboxEntry is { } entry)
        {
            InboxMessages.Add(new InboxMessage { MessageId = entry.MessageId, Consumer = entry.Consumer, ProcessedAt = now });
        }

        var outboxCount = 0;
        foreach (var outbound in pendingOutbox.Drain())
        {
            OutboxMessages.Add(OutboxMessage.From(outbound, now));
            outboxCount++;
        }

        int written;
        try
        {
            written = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // AZURE/SQL: the rowversion column changed since the read, so UPDATE ... WHERE [Version] = @v
            // found no row. EF Core checks the number of affected rows and throws this exception.
            var entity = exception.Entries.FirstOrDefault();
            throw new ConcurrencyConflictException(
                entity?.Metadata.ClrType.Name ?? "Entity",
                entity is null ? "?" : string.Join(",", entity.Properties.Where(p => p.Metadata.IsPrimaryKey()).Select(p => p.CurrentValue)),
                exception);
        }
        catch (DbUpdateException exception) when (inboxEntry is { } duplicate && IsInboxViolation(exception))
        {
            throw new DuplicateMessageException(duplicate, exception);
        }

        if (outboxCount > 0)
        {
            // WHY: we do not publish from this method. Sending to the broker inside the request would extend it by a
            // round trip to Service Bus, and a send failure would have to be handled by the poller anyway. Instead we
            // wake up this instance's poller: the message goes out milliseconds after the commit, and the publishing,
            // retry and claiming logic exists in only one place.
            outboxSignal.Notify();
        }

        return written;
    }

    private static bool IsInboxViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: PrimaryKeyViolation or UniqueIndexViolation }
        && exception.Entries.Any(entry => entry.Entity is InboxMessage);
}
