using System.Text.Json;
using ShopFlow.SharedKernel.Domain;
using ShopFlow.SharedKernel.Persistence;

namespace ShopFlow.Messaging.Failures;

public enum FailureKind
{
    /// <summary>Retrying makes sense: timeout, throttling, version conflict.</summary>
    Transient = 0,

    /// <summary>Retrying gives the same result: bad payload, violated business rule.</summary>
    Permanent = 1,
}

public readonly record struct FailureClassification(FailureKind Kind, string Reason, string Description)
{
    public static FailureClassification Transient(string reason, string description) =>
        new(FailureKind.Transient, reason, description);

    public static FailureClassification Permanent(string reason, string description) =>
        new(FailureKind.Permanent, reason, description);
}

/// <summary>
/// Classification rule for one family of exceptions. Each adapter (Cosmos, SQL, Service Bus)
/// registers its own, because only the adapter knows what its error codes mean.
/// </summary>
public interface IFailureClassifier
{
    bool TryClassify(Exception exception, out FailureClassification classification);
}

/// <summary>
/// Decides whether, after an exception, the message goes back to the queue or to the DLQ.
/// </summary>
/// <remarks>
/// WHY: this is the most important decision in poison message handling. A permanent error treated as
/// transient burns through all attempts, blocks the session and only then lands in the DLQ with the
/// unhelpful MaxDeliveryCountExceeded reason. A transient error treated as permanent dead-letters a
/// valid message at the first database hiccup. The default is transient: an unknown exception more
/// often means a failing dependency than a broken message, and the broker's delivery limit caps the
/// number of retries anyway.
/// </remarks>
public sealed class MessageFailureClassifier(IEnumerable<IFailureClassifier> classifiers)
{
    private readonly IFailureClassifier[] _classifiers = [.. classifiers];

    public FailureClassification Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // The cause is often wrapped (e.g. DbUpdateException → SqlException), so we check the whole chain.
        for (var current = exception; current is not null; current = current.InnerException)
        {
            foreach (var classifier in _classifiers)
            {
                if (classifier.TryClassify(current, out var classification))
                {
                    return classification;
                }
            }
        }

        return FailureClassification.Transient("UnhandledException", exception.Message);
    }
}

/// <summary>Infrastructure-independent rules.</summary>
internal sealed class DefaultFailureClassifier : IFailureClassifier
{
    public bool TryClassify(Exception exception, out FailureClassification classification)
    {
        switch (exception)
        {
            case PermanentMessageException permanent:
                classification = FailureClassification.Permanent(permanent.Reason, permanent.Message);
                return true;

            case JsonException:
                classification = FailureClassification.Permanent(DeadLetterReasons.DeserializationFailed, exception.Message);
                return true;

            // A violated domain invariant will not disappear on the second attempt.
            case DomainException domain:
                classification = FailureClassification.Permanent(
                    DeadLetterReasons.BusinessRuleViolation,
                    $"{domain.Code}: {domain.Message}");
                return true;

            // Someone else changed the aggregate between our read and our write. The second attempt reads
            // the fresh state and will most likely succeed.
            case ConcurrencyConflictException:
                classification = FailureClassification.Transient("ConcurrencyConflict", exception.Message);
                return true;

            case OutOfOrderMessageException:
                classification = FailureClassification.Transient("OutOfOrder", exception.Message);
                return true;

            case TimeoutException or OperationCanceledException:
                classification = FailureClassification.Transient("Timeout", exception.Message);
                return true;

            default:
                classification = default;
                return false;
        }
    }
}
