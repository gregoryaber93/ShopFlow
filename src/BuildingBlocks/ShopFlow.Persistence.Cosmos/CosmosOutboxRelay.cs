using System.Text.Json;
using Microsoft.Extensions.Logging;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Diagnostics;
using ShopFlow.Messaging.Outbox;
using ShopFlow.Persistence.Cosmos.Documents;

namespace ShopFlow.Persistence.Cosmos;

/// <summary>
/// Turns a batch of Change Feed changes into published messages. Called by a function with a Cosmos DB
/// trigger in every service that uses the Cosmos outbox.
/// </summary>
/// <remarks>
/// AZURE: the Change Feed is a durable, ordered log of a container's changes. The Functions trigger reads
/// it through the Change Feed Processor: every physical partition has a lease (a document in the
/// <c>leases</c> container) that remembers the read position. Change order is guaranteed within a logical
/// partition key, i.e. within an order in our case.
/// <para>
/// The delivery guarantee is "at least once": if the function publishes a batch and crashes before saving
/// its position, it receives the same batch again. Duplicates are removed by Service Bus duplicate detection
/// (MessageId = outbox record identifier) and, after its window, by the consumer's inbox.
/// </para>
/// </remarks>
public sealed partial class CosmosOutboxRelay(
    IMessagePublisher publisher,
    TimeProvider timeProvider,
    ILogger<CosmosOutboxRelay> logger)
{
    /// <summary>Publishes the outbox records found in a batch of changes. Returns the number of published messages.</summary>
    /// <param name="changes">Change Feed documents: aggregates, outbox and inbox mixed together.</param>
    /// <param name="source">Container name, for metric labels.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<int> RelayAsync(IReadOnlyList<JsonElement> changes, string source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var documents = new List<OutboxDocument>();

        foreach (var change in changes)
        {
            if (!IsOutboxDocument(change))
            {
                continue;
            }

            try
            {
                documents.Add(change.Deserialize<OutboxDocument>(CosmosJson.Options)
                    ?? throw new JsonException("Outbox document is JSON null."));
            }
            catch (JsonException exception)
            {
                // WHY: an exception would stop this Change Feed partition forever, because the trigger retries the
                // batch until it succeeds. One broken document must not block the events of all orders in this
                // physical partition. We skip it and log at Critical level; the alert on this entry is defined
                // in infra/modules/operations/alerts.bicep.
                LogMalformedOutboxDocument(exception, change.TryGetProperty("id", out var id) ? id.GetString() : null, source);
            }
        }

        if (documents.Count == 0)
        {
            return 0;
        }

        OutboundMessage[] messages = [.. documents.Select(document => document.ToOutboundMessage())];

        // An exception from here must propagate: the trigger will not advance the position and will retry the whole batch.
        await publisher.PublishAsync(messages, cancellationToken).ConfigureAwait(false);

        var now = timeProvider.GetUtcNow();
        foreach (var document in documents)
        {
            MessagingTelemetry.RecordOutboxLag(now - document.CreatedAt, source);
        }

        LogRelayed(documents.Count, changes.Count, source);
        return documents.Count;
    }

    private static bool IsOutboxDocument(JsonElement change) =>
        change.ValueKind == JsonValueKind.Object
        && change.TryGetProperty("docType", out var docType)
        && docType.ValueEquals(OutboxDocument.DocumentType);

    [LoggerMessage(Level = LogLevel.Information, Message = "Relayed {Published} outbox message(s) out of {Changes} change(s) from {Source}")]
    private partial void LogRelayed(int published, int changes, string source);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Outbox document {DocumentId} in {Source} is malformed and was skipped; its message was NOT published")]
    private partial void LogMalformedOutboxDocument(Exception exception, string? documentId, string source);
}
