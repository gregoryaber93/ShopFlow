using Microsoft.Data.SqlClient;
using ShopFlow.Messaging.Failures;

namespace ShopFlow.Persistence.SqlServer;

/// <summary>What SQL Server error numbers mean for the "retry or dead-letter" decision.</summary>
internal sealed class SqlFailureClassifier : IFailureClassifier
{
    // Errors caused by the message content: a retry gives the same result.
    private static readonly HashSet<int> PermanentErrors =
    [
        547,  // foreign key or CHECK constraint violation
        245,  // type conversion error
        8114, // parameter conversion error
        2628, // string truncation (string or binary data would be truncated)
        8152, // as above, older message version
    ];

    public bool TryClassify(Exception exception, out FailureClassification classification)
    {
        if (exception is not SqlException sqlException)
        {
            classification = default;
            return false;
        }

        classification = PermanentErrors.Contains(sqlException.Number)
            ? FailureClassification.Permanent($"Sql.{sqlException.Number}", sqlException.Message)

            // AZURE: the rest are transient or operational, including ones typical for Azure SQL:
            // 40613 (database unavailable, e.g. during failover), 40197 and 40501 (service busy, throttling),
            // 49918–49920 (not enough resources), 1205 (deadlock), -2 (client timeout).
            // Also 2627/2601 (duplicate key): when two handlers insert the same natural key at the same time,
            // the second attempt reads the already existing row and takes the idempotent path.
            : FailureClassification.Transient($"Sql.{sqlException.Number}", sqlException.Message);

        return true;
    }
}
