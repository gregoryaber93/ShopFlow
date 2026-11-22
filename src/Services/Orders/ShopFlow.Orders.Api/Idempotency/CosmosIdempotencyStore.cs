using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using ShopFlow.Orders.Infrastructure.Cosmos;
using ShopFlow.Persistence.Cosmos;
using ShopFlow.Persistence.Cosmos.Documents;
using ShopFlow.Web.Idempotency;

namespace ShopFlow.Orders.Api.Idempotency;

public sealed class IdempotencyDocument : CosmosDocument
{
    public const string DocumentType = "idempotency";

    [JsonPropertyName("docType")]
    public override string DocType => DocumentType;

    /// <summary>Partition key: hash of the key scope (identical to <see cref="CosmosDocument.Id"/>).</summary>
    public required string ScopeKey { get; init; }

    /// <summary>The original "customer:key" scope, for diagnostics.</summary>
    public required string RawScope { get; init; }

    public required string RequestHash { get; init; }

    public required IdempotencyRecordState State { get; init; }

    public required DateTimeOffset LockedUntil { get; init; }

    public StoredResponse? Response { get; init; }
}

/// <summary>
/// Idempotency keys in the <c>idempotency</c> container, shared by all orders-api instances.
/// </summary>
/// <remarks>
/// AZURE: a document identifier cannot contain the / \ ? # characters, and the client makes up the idempotency
/// key. That is why the identifier (and the partition key) is a SHA-256 hash of the scope, not the scope itself.
/// Records expire after 24 hours through TTL. A client that retries a request after a day gets a new execution;
/// in practice retries take seconds or minutes.
/// </remarks>
internal sealed class CosmosIdempotencyStore(OrdersContainers containers, TimeProvider timeProvider) : IIdempotencyStore
{
    private static readonly int TimeToLiveSeconds = (int)TimeSpan.FromHours(24).TotalSeconds;

    private Container Container => containers.Idempotency;

    public async Task<IdempotencyRecord?> TryBeginAsync(string scopeKey, string requestHash, TimeSpan lockDuration, CancellationToken cancellationToken)
    {
        var key = Hash(scopeKey);
        var now = timeProvider.GetUtcNow();
        var candidate = InProgress(key, scopeKey, requestHash, now + lockDuration);

        try
        {
            // AZURE: Create ends with 409 when a document with this id already exists in the partition.
            // This is an atomic "create if not exists": of two concurrent requests exactly one wins.
            await Container.CreateItemAsync(candidate, new PartitionKey(key), WriteOptions(), cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await Container.ReadOrDefaultAsync<IdempotencyDocument>(key, key, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                // The record disappeared between Create and Read (released after an error). The client's next attempt will create it.
                return new IdempotencyRecord(scopeKey, requestHash, IdempotencyRecordState.InProgress, now + lockDuration, null);
            }

            return await TryTakeOverAsync(existing, candidate, now, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task CompleteAsync(string scopeKey, string requestHash, StoredResponse response, CancellationToken cancellationToken)
    {
        var key = Hash(scopeKey);
        var document = new IdempotencyDocument
        {
            Id = key,
            ScopeKey = key,
            RawScope = scopeKey,
            RequestHash = requestHash,
            State = IdempotencyRecordState.Completed,
            LockedUntil = timeProvider.GetUtcNow(),
            Response = response,
            TimeToLive = TimeToLiveSeconds,
        };

        return Container.UpsertItemAsync(document, new PartitionKey(key), WriteOptions(), cancellationToken);
    }

    public async Task ReleaseAsync(string scopeKey, CancellationToken cancellationToken)
    {
        var key = Hash(scopeKey);

        try
        {
            await Container.DeleteItemAsync<IdempotencyDocument>(key, new PartitionKey(key), WriteOptions(), cancellationToken).ConfigureAwait(false);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }

    /// <summary>
    /// Takes over a record abandoned by an instance that crashed during the request (the claim expired).
    /// </summary>
    private async Task<IdempotencyRecord?> TryTakeOverAsync(IdempotencyDocument existing, IdempotencyDocument candidate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var abandoned = existing.State == IdempotencyRecordState.InProgress
            && existing.LockedUntil < now
            && existing.RequestHash == candidate.RequestHash;

        if (abandoned)
        {
            try
            {
                // If-Match: only one of the instances that noticed the expired claim at the same time will take it over.
                var options = WriteOptions();
                options.IfMatchEtag = existing.ETag;
                await Container.ReplaceItemAsync(candidate, candidate.Id, new PartitionKey(candidate.ScopeKey), options, cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                // Someone was faster; we treat the record as in progress.
            }
        }

        return new IdempotencyRecord(existing.RawScope, existing.RequestHash, existing.State, existing.LockedUntil, existing.Response);
    }

    private static IdempotencyDocument InProgress(string key, string rawScope, string requestHash, DateTimeOffset lockedUntil) => new()
    {
        Id = key,
        ScopeKey = key,
        RawScope = rawScope,
        RequestHash = requestHash,
        State = IdempotencyRecordState.InProgress,
        LockedUntil = lockedUntil,
        TimeToLive = TimeToLiveSeconds,
    };

    private static ItemRequestOptions WriteOptions() => new() { EnableContentResponseOnWrite = false };

    private static string Hash(string scopeKey) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scopeKey)));
}
