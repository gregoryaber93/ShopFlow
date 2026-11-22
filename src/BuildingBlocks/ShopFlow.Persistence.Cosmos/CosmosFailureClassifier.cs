using System.Net;
using Microsoft.Azure.Cosmos;
using ShopFlow.Messaging.Failures;

namespace ShopFlow.Persistence.Cosmos;

/// <summary>What Cosmos DB status codes mean for the "retry or dead-letter" decision.</summary>
internal sealed class CosmosFailureClassifier : IFailureClassifier
{
    public bool TryClassify(Exception exception, out FailureClassification classification)
    {
        HttpStatusCode statusCode;
        switch (exception)
        {
            case CosmosException cosmosException:
                statusCode = cosmosException.StatusCode;
                break;
            case CosmosBatchException batchException:
                statusCode = batchException.StatusCode;
                break;
            default:
                classification = default;
                return false;
        }

        classification = statusCode switch
        {
            // AZURE: 400 and 413 mean our document is bad (invalid JSON, missing partition key,
            // over 2 MB). The next attempt would send the same thing.
            HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge => FailureClassification.Permanent(
                $"Cosmos.{(int)statusCode}",
                exception.Message),

            // AZURE: everything else is transient or operational. 429 = RU/s exceeded (the SDK already retried
            // and gave up), 408 = timeout, 503 = service unavailable, 410 = partition move,
            // 449 = write conflict to retry, 403 = missing RBAC role (deployment error).
            _ => FailureClassification.Transient($"Cosmos.{(int)statusCode}", exception.Message),
        };

        return true;
    }
}
