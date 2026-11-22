using System.Net;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using ShopFlow.Notifications.Application;
using ShopFlow.Persistence.Cosmos;
using ShopFlow.Persistence.Cosmos.Documents;
using ShopFlow.SharedKernel.Persistence;

namespace ShopFlow.Notifications.Infrastructure.Cosmos;

public static class NotificationsContainers
{
    public const string DatabaseName = "notifications";

    /// <summary>Send history, partitioned by <c>/customerId</c>.</summary>
    /// <remarks>
    /// AZURE: the most common reads are "what did we send to this customer" (customer support) and a point read of one
    /// notification (idempotency). Both hit one partition. Operations alerts do not concern a customer, so they get the
    /// synthetic value <c>operations</c>; there are few of them, so they do not create a hot partition.
    /// Container TTL: 180 days (the period for complaints and "I did not get the e-mail" disputes).
    /// </remarks>
    public const string LogName = "notification-log";

    public const string PartitionKey = "customerId";
}

internal sealed class NotificationLogDocument : CosmosDocument
{
    public const string DocumentType = "notification";

    [JsonPropertyName("docType")]
    public override string DocType => DocumentType;

    [JsonPropertyName(NotificationsContainers.PartitionKey)]
    public required string PartitionKey { get; init; }

    [JsonPropertyName("template")]
    public required string Template { get; init; }

    [JsonPropertyName("channel")]
    public required string Channel { get; init; }

    [JsonPropertyName("orderId")]
    public Guid? OrderId { get; init; }

    [JsonPropertyName("sourceMessageId")]
    public required Guid SourceMessageId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("attempt")]
    public required int Attempt { get; init; }

    [JsonPropertyName("claimedAt")]
    public required DateTimeOffset ClaimedAt { get; init; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; init; }

    [JsonPropertyName("recipient")]
    public string? Recipient { get; init; }

    [JsonPropertyName("providerMessageId")]
    public string? ProviderMessageId { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    public bool IsCompleted => Status != nameof(NotificationStatus.Pending);
}

/// <summary>
/// Send history in Cosmos DB as the idempotency mechanism.
/// </summary>
/// <remarks>
/// AZURE: a reservation is a <c>CreateItem</c>. The identifier is unique within the partition, so a second write of the
/// same notification ends with 409 Conflict without a separate "check if it exists". Taking over an interrupted
/// reservation and recording the outcome is a <c>ReplaceItem</c> with If-Match: when another instance changed the entry in
/// the meantime, Cosmos answers 412 and neither of them overwrites the other's outcome.
/// </remarks>
internal sealed class CosmosNotificationLog(Database database, TimeProvider timeProvider) : INotificationLog
{
    /// <summary>
    /// How long a reservation protects a send from being taken over by another instance.
    /// </summary>
    /// <remarks>
    /// WHY: longer than the longest realistic send (the ACS client timeout with retries), shorter than the interval after
    /// which Service Bus redelivers the message after an instance crash. A fresh reservation means someone is sending right
    /// now, so we report a conflict and the message goes back to the queue. An old one means that attempt died.
    /// </remarks>
    internal static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan TimeToLive = TimeSpan.FromDays(180);

    private readonly Container _container = database.GetContainer(NotificationsContainers.LogName);

    public async Task<bool> IsCompletedAsync(NotificationDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var existing = await _container
            .ReadOrDefaultAsync<NotificationLogDocument>(draft.NotificationId.ToString("D"), draft.PartitionKey, cancellationToken)
            .ConfigureAwait(false);

        return existing?.IsCompleted == true;
    }

    public async Task<NotificationClaim?> TryClaimAsync(NotificationDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var now = timeProvider.GetUtcNow();
        var id = draft.NotificationId.ToString("D");
        var partitionKey = new PartitionKey(draft.PartitionKey);

        try
        {
            var created = await _container
                .CreateItemAsync(NewClaim(draft, attempt: 1, now), partitionKey, WithoutContent(), cancellationToken)
                .ConfigureAwait(false);

            CosmosTelemetry.RecordRequestCharge(_container.Id, "notification_claim", created.RequestCharge);
            return new NotificationClaim(draft, 1, now, created.ETag);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            CosmosTelemetry.RecordRequestCharge(_container.Id, "notification_claim", exception.RequestCharge);
        }

        var existing = await _container
            .ReadOrDefaultAsync<NotificationLogDocument>(id, draft.PartitionKey, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ConcurrencyConflictException("Notification", id); // deleted between the write and the read

        if (existing.IsCompleted)
        {
            return null;
        }

        if (now - existing.ClaimedAt < ClaimLease)
        {
            throw new ConcurrencyConflictException("Notification", id);
        }

        try
        {
            var replaced = await _container
                .ReplaceItemAsync(
                    NewClaim(draft, existing.Attempt + 1, now),
                    id,
                    partitionKey,
                    WithoutContent(existing.ETag),
                    cancellationToken)
                .ConfigureAwait(false);

            CosmosTelemetry.RecordRequestCharge(_container.Id, "notification_reclaim", replaced.RequestCharge);
            return new NotificationClaim(draft, existing.Attempt + 1, now, replaced.ETag);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new ConcurrencyConflictException("Notification", id, exception);
        }
    }

    public async Task CompleteAsync(NotificationClaim claim, NotificationCompletion completion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(completion);

        var draft = claim.Draft;
        var id = draft.NotificationId.ToString("D");

        var completed = new NotificationLogDocument
        {
            Id = id,
            PartitionKey = draft.PartitionKey,
            Template = draft.Template,
            Channel = draft.Channel.ToString(),
            OrderId = draft.OrderId,
            SourceMessageId = draft.SourceMessageId,
            Status = completion.Status.ToString(),
            Attempt = claim.Attempt,
            ClaimedAt = claim.ClaimedAt,
            CompletedAt = timeProvider.GetUtcNow(),
            Recipient = completion.MaskedRecipient,
            ProviderMessageId = completion.ProviderMessageId,
            Reason = completion.Reason,
            TimeToLive = (int)TimeToLive.TotalSeconds,
        };

        try
        {
            var response = await _container
                .ReplaceItemAsync(completed, id, new PartitionKey(draft.PartitionKey), WithoutContent(claim.ETag), cancellationToken)
                .ConfigureAwait(false);

            CosmosTelemetry.RecordRequestCharge(_container.Id, "notification_complete", response.RequestCharge);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            // Another instance took over the reservation and recorded its outcome. The message has already been sent (by us or by
            // it), so there is nothing to retry; throwing would send the e-mail once more.
            CosmosTelemetry.RecordRequestCharge(_container.Id, "notification_complete", exception.RequestCharge);
        }
    }

    private static NotificationLogDocument NewClaim(NotificationDraft draft, int attempt, DateTimeOffset now) => new()
    {
        Id = draft.NotificationId.ToString("D"),
        PartitionKey = draft.PartitionKey,
        Template = draft.Template,
        Channel = draft.Channel.ToString(),
        OrderId = draft.OrderId,
        SourceMessageId = draft.SourceMessageId,
        Status = nameof(NotificationStatus.Pending),
        Attempt = attempt,
        ClaimedAt = now,
        TimeToLive = (int)TimeToLive.TotalSeconds,
    };

    private static ItemRequestOptions WithoutContent(string? ifMatch = null) =>
        new() { EnableContentResponseOnWrite = false, IfMatchEtag = ifMatch };
}

/// <summary>A customer's notification history for customer support.</summary>
public sealed record NotificationHistoryItem(
    Guid NotificationId,
    string Template,
    string Channel,
    Guid? OrderId,
    string Status,
    int Attempt,
    DateTimeOffset ClaimedAt,
    DateTimeOffset? CompletedAt,
    string? Recipient,
    string? Reason);

public sealed class NotificationHistoryReader(Database database)
{
    private readonly Container _container = database.GetContainer(NotificationsContainers.LogName);

    /// <remarks>
    /// AZURE: a single-partition query with <c>ORDER BY</c> on one field uses the default range index.
    /// <c>TOP</c> caps the RU cost, because a customer with a long history could have hundreds of entries.
    /// </remarks>
    public Task<IReadOnlyList<NotificationHistoryItem>> GetForCustomerAsync(Guid customerId, int top, CancellationToken cancellationToken)
    {
        var query = new QueryDefinition(
                "SELECT TOP @top c.id AS notificationId, c.template, c.channel, c.orderId, c.status, c.attempt, c.claimedAt, c.completedAt, c.recipient, c.reason " +
                "FROM c WHERE c.docType = @docType ORDER BY c.claimedAt DESC")
            .WithParameter("@top", Math.Clamp(top, 1, 200))
            .WithParameter("@docType", NotificationLogDocument.DocumentType);

        return _container.QuerySinglePartitionAsync<NotificationHistoryItem>(query, customerId.ToString("D"), cancellationToken);
    }
}
