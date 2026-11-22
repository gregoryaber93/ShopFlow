namespace ShopFlow.SharedKernel.Persistence;

/// <summary>
/// Write rejected because someone changed the object since it was read (optimistic concurrency).
/// </summary>
/// <remarks>
/// AZURE: in Cosmos DB this is a 412 Precondition Failed response to a request with an If-Match header;
/// in Azure SQL it is zero affected rows for a condition on the rowversion column.
/// WHY: one exception type for both stores, so that the Application layer and the mapping to HTTP 412
/// do not depend on where the data lives.
/// </remarks>
public sealed class ConcurrencyConflictException : Exception
{
    public ConcurrencyConflictException(string resource, string key)
        : base($"{resource} '{key}' was modified by another operation.")
    {
        Resource = resource;
        Key = key;
    }

    public ConcurrencyConflictException(string resource, string key, Exception innerException)
        : base($"{resource} '{key}' was modified by another operation.", innerException)
    {
        Resource = resource;
        Key = key;
    }

    public string Resource { get; }

    public string Key { get; }
}
