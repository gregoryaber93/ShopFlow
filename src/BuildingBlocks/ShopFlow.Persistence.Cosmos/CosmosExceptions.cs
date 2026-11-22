using System.Net;

namespace ShopFlow.Persistence.Cosmos;

/// <summary>Attempt to create a document whose identifier already exists in this partition.</summary>
public sealed class DocumentAlreadyExistsException : Exception
{
    public DocumentAlreadyExistsException(string documentType, string id)
        : base($"Document '{id}' of type '{documentType}' already exists.")
    {
        DocumentType = documentType;
        Id = id;
    }

    public DocumentAlreadyExistsException(string documentType, string id, Exception innerException)
        : base($"Document '{id}' of type '{documentType}' already exists.", innerException)
    {
        DocumentType = documentType;
        Id = id;
    }

    public string DocumentType { get; }

    public string Id { get; }
}

/// <summary>
/// TransactionalBatch rejected for a reason unrelated to the data (throttling, timeout, outage).
/// </summary>
/// <remarks>
/// AZURE: unlike single-document operations, <c>TransactionalBatch.ExecuteAsync</c> does not throw a
/// <c>CosmosException</c> on failure; it returns a response with a status code. This exception restores
/// a uniform error model so that the classifier has something to classify.
/// </remarks>
public sealed class CosmosBatchException : Exception
{
    public CosmosBatchException(HttpStatusCode statusCode, string? errorMessage, TimeSpan? retryAfter, double requestCharge)
        : base($"Cosmos DB transactional batch failed with status {(int)statusCode} ({statusCode}): {errorMessage}")
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        RequestCharge = requestCharge;
    }

    public HttpStatusCode StatusCode { get; }

    public TimeSpan? RetryAfter { get; }

    public double RequestCharge { get; }
}
