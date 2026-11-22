using Azure.Messaging.ServiceBus;
using ShopFlow.Messaging.Failures;

namespace ShopFlow.Messaging.ServiceBus;

/// <summary>What Service Bus errors mean for the "retry or dead-letter" decision.</summary>
internal sealed class ServiceBusFailureClassifier : IFailureClassifier
{
    public bool TryClassify(Exception exception, out FailureClassification classification)
    {
        if (exception is not ServiceBusException serviceBusException)
        {
            classification = default;
            return false;
        }

        classification = serviceBusException.Reason switch
        {
            // The handler tried to send a message larger than the entity limit. A retry gives the same result.
            ServiceBusFailureReason.MessageSizeExceeded => FailureClassification.Permanent(
                DeadLetterReasons.MessageTooLarge,
                serviceBusException.Message),

            // AZURE: the remaining reasons are transient or operational: ServiceBusy (throttling),
            // ServiceTimeout, ServiceCommunicationProblem, MessageLockLost, SessionLockLost,
            // QuotaExceeded (entity full), MessagingEntityNotFound and missing permissions (deployment error).
            // None of them is the message's fault, so the message must go back to the queue.
            _ => FailureClassification.Transient(
                $"ServiceBus.{serviceBusException.Reason}",
                serviceBusException.Message),
        };

        return true;
    }
}
