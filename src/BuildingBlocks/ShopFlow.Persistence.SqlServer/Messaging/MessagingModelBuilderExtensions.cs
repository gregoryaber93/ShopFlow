using Microsoft.EntityFrameworkCore;

namespace ShopFlow.Persistence.SqlServer.Messaging;

public static class MessagingModelBuilderExtensions
{
    public const string Schema = "messaging";

    /// <summary>Adds the outbox and inbox tables to the service's context model (and therefore to its migrations).</summary>
    public static ModelBuilder AddShopFlowMessagingTables(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("OutboxMessages", Schema);
            outbox.HasKey(message => message.Sequence);
            outbox.Property(message => message.Sequence).UseIdentityColumn();

            outbox.HasIndex(message => message.MessageId).IsUnique();

            // The filtered index covers exactly the poller's query and stays small,
            // because published rows drop out of it.
            outbox.HasIndex(message => message.Sequence)
                .HasDatabaseName("IX_OutboxMessages_Pending")
                .HasFilter("[PublishedAt] IS NULL")
                .IncludeProperties(message => new { message.ClaimedUntil, message.Attempts });

            outbox.HasIndex(message => message.PublishedAt)
                .HasDatabaseName("IX_OutboxMessages_PublishedAt")
                .HasFilter("[PublishedAt] IS NOT NULL");

            outbox.Property(message => message.MessageType).HasMaxLength(128).IsUnicode(false);
            outbox.Property(message => message.CorrelationId).HasMaxLength(128).IsUnicode(false);
            outbox.Property(message => message.CausationId).HasMaxLength(128).IsUnicode(false);
            outbox.Property(message => message.Source).HasMaxLength(64).IsUnicode(false);
            outbox.Property(message => message.SessionId).HasMaxLength(128).IsUnicode(false);
            outbox.Property(message => message.TraceParent).HasMaxLength(128).IsUnicode(false);
            outbox.Property(message => message.TraceState).HasMaxLength(512).IsUnicode(false);
            outbox.Property(message => message.Destination).HasMaxLength(260).IsUnicode(false);
            outbox.Property(message => message.ClaimedBy).HasMaxLength(128).IsUnicode(false);
            outbox.Property(message => message.LastError).HasMaxLength(2000);
            outbox.Property(message => message.Payload).IsRequired();
        });

        modelBuilder.Entity<InboxMessage>(inbox =>
        {
            inbox.ToTable("InboxMessages", Schema);

            // WHY: composite key with the consumer first. Inserting a duplicate violates the primary key
            // (error 2627), which the adapter translates into DuplicateMessageException.
            inbox.HasKey(message => new { message.Consumer, message.MessageId });
            inbox.Property(message => message.Consumer).HasMaxLength(64).IsUnicode(false);
            inbox.HasIndex(message => message.ProcessedAt);
        });

        return modelBuilder;
    }
}
