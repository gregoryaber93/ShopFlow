namespace ShopFlow.Web.Idempotency;

/// <summary>Remembered response to a request with an idempotency key.</summary>
public sealed record StoredResponse(
    int StatusCode,
    string? ContentType,
    string? Location,
    string? ETag,
    byte[] Body);

public enum IdempotencyRecordState
{
    InProgress = 0,
    Completed = 1,
}

public sealed record IdempotencyRecord(
    string ScopeKey,
    string RequestHash,
    IdempotencyRecordState State,
    DateTimeOffset LockedUntil,
    StoredResponse? Response);

/// <summary>Idempotency key store. The Cosmos DB implementation lives in Orders.Infrastructure.</summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically creates a record in the <see cref="IdempotencyRecordState.InProgress"/> state.
    /// Returns <c>null</c> if the record was created (the request should be executed), or the existing record.
    /// </summary>
    /// <remarks>
    /// Atomicity is crucial: two concurrent requests with the same key (the customer clicked twice) must not both
    /// hear "you own it". In Cosmos DB this is guaranteed by inserting a document with the same <c>id</c> (the second
    /// gets 409). An expired claim left by a crashed instance is taken over with a conditional replace using If-Match.
    /// </remarks>
    Task<IdempotencyRecord?> TryBeginAsync(string scopeKey, string requestHash, TimeSpan lockDuration, CancellationToken cancellationToken);

    Task CompleteAsync(string scopeKey, string requestHash, StoredResponse response, CancellationToken cancellationToken);

    /// <summary>Removes the record so that the client can retry the request with the same key.</summary>
    Task ReleaseAsync(string scopeKey, CancellationToken cancellationToken);
}
