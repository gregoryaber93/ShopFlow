using System.Collections.Concurrent;

namespace ShopFlow.Web.Idempotency;

/// <summary>
/// In-process store. For tests only: with several instances behind a load balancer, a retry that lands on
/// another instance will not see the key.
/// </summary>
public sealed class InMemoryIdempotencyStore(TimeProvider timeProvider) : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, IdempotencyRecord> _records = new(StringComparer.Ordinal);

    public Task<IdempotencyRecord?> TryBeginAsync(string scopeKey, string requestHash, TimeSpan lockDuration, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var candidate = new IdempotencyRecord(scopeKey, requestHash, IdempotencyRecordState.InProgress, now + lockDuration, null);

        while (true)
        {
            if (_records.TryAdd(scopeKey, candidate))
            {
                return Task.FromResult<IdempotencyRecord?>(null);
            }

            if (!_records.TryGetValue(scopeKey, out var existing))
            {
                continue;
            }

            // Taking over an abandoned claim (the instance crashed during the request).
            var abandoned = existing.State == IdempotencyRecordState.InProgress && existing.LockedUntil < now;
            if (abandoned && existing.RequestHash == requestHash && _records.TryUpdate(scopeKey, candidate, existing))
            {
                return Task.FromResult<IdempotencyRecord?>(null);
            }

            return Task.FromResult<IdempotencyRecord?>(existing);
        }
    }

    public Task CompleteAsync(string scopeKey, string requestHash, StoredResponse response, CancellationToken cancellationToken)
    {
        _records[scopeKey] = new IdempotencyRecord(scopeKey, requestHash, IdempotencyRecordState.Completed, DateTimeOffset.MaxValue, response);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string scopeKey, CancellationToken cancellationToken)
    {
        _records.TryRemove(scopeKey, out _);
        return Task.CompletedTask;
    }
}
