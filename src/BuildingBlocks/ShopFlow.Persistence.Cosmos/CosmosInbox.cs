using System.Net;
using Microsoft.Azure.Cosmos;
using ShopFlow.Messaging.Inbox;
using ShopFlow.Persistence.Cosmos.Documents;

namespace ShopFlow.Persistence.Cosmos;

/// <summary>
/// Standalone inbox for handlers without an aggregate of their own.
/// The container is partitioned by message identifier.
/// </summary>
/// <remarks>
/// WHY: good enough where "at most once" or "at least once" is good enough. Sending notifications needs
/// more (reservation, send, outcome and a check with the provider), so Notifications keeps its own send
/// history instead of this inbox.
/// </remarks>
public sealed class CosmosInbox(
    Container container,
    string partitionKeyProperty,
    TimeSpan timeToLive,
    TimeProvider timeProvider) : IInbox
{
    public async Task<bool> TryRegisterAsync(InboxEntry entry, CancellationToken cancellationToken)
    {
        var partitionKeyValue = entry.MessageId.ToString("D");
        var document = InboxDocument.From(entry, partitionKeyProperty, partitionKeyValue, timeProvider.GetUtcNow(), timeToLive);

        try
        {
            var response = await container
                .CreateItemAsync(
                    document,
                    new PartitionKey(partitionKeyValue),
                    new ItemRequestOptions { EnableContentResponseOnWrite = false },
                    cancellationToken)
                .ConfigureAwait(false);

            CosmosTelemetry.RecordRequestCharge(container.Id, "inbox_create", response.RequestCharge);
            return true;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            CosmosTelemetry.RecordRequestCharge(container.Id, "inbox_create", exception.RequestCharge);
            return false;
        }
    }
}
