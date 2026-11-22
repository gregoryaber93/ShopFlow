using Azure.Messaging.ServiceBus;
using ShopFlow.BuildingBlocks.Tests.Messaging;
using ShopFlow.Contracts;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.ServiceBus;

namespace ShopFlow.BuildingBlocks.Tests.ServiceBus;

public sealed class ServiceBusEnvelopeMapperTests
{
    [Fact]
    public void Outgoing_message_carries_routing_and_dedup_metadata_in_broker_fields()
    {
        using var provider = MessagingTestHost.Build();
        var envelope = MessagingTestHost.Envelope(provider, MessagingTestHost.SampleOrder()) with { CausationId = "cause-1" };

        var message = ServiceBusEnvelopeMapper.ToServiceBusMessage(envelope);

        message.MessageId.ShouldBe(envelope.MessageId.ToString("D"));
        message.Subject.ShouldBe("TestOrderPlaced");
        message.SessionId.ShouldBe(envelope.SessionId);
        message.CorrelationId.ShouldBe(envelope.CorrelationId);

        // Correlation filters compare strings, so the version must be a string.
        message.ApplicationProperties[MessageHeaders.ContractVersion].ShouldBe("1");
        message.ApplicationProperties[MessageHeaders.CausationId].ShouldBe("cause-1");
    }

    [Fact]
    public void Received_message_maps_back_to_the_same_envelope()
    {
        using var provider = MessagingTestHost.Build();
        var original = MessagingTestHost.Envelope(provider, MessagingTestHost.SampleOrder());
        var sent = ServiceBusEnvelopeMapper.ToServiceBusMessage(original);

        var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: sent.Body,
            messageId: sent.MessageId,
            sessionId: sent.SessionId,
            correlationId: sent.CorrelationId,
            subject: sent.Subject,
            properties: sent.ApplicationProperties,
            deliveryCount: 3,
            enqueuedTime: MessagingTestHost.Now);

        var envelope = ServiceBusEnvelopeMapper.ToEnvelope(received);

        envelope.MessageId.ShouldBe(original.MessageId);
        envelope.MessageType.ShouldBe(original.MessageType);
        envelope.ContractVersion.ShouldBe(original.ContractVersion);
        envelope.SessionId.ShouldBe(original.SessionId);
        envelope.OccurredAt.ShouldBe(original.OccurredAt);
        envelope.Payload.ToArray().ShouldBe(original.Payload.ToArray());
        ServiceBusEnvelopeMapper.ToDeliveryInfo(received).DeliveryCount.ShouldBe(3);
    }

    [Fact]
    public void Message_without_contract_version_is_malformed()
    {
        var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("{}"),
            messageId: Guid.NewGuid().ToString("D"),
            subject: "TestOrderPlaced");

        var exception = Should.Throw<PermanentMessageException>(() => ServiceBusEnvelopeMapper.ToEnvelope(received));

        exception.Reason.ShouldBe(DeadLetterReasons.MalformedEnvelope);
    }

    [Fact]
    public void Message_replayed_from_dlq_is_flagged()
    {
        var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("{}"),
            messageId: Guid.NewGuid().ToString("D"),
            properties: new Dictionary<string, object> { [MessageHeaders.ReplayedFromDeadLetter] = true },
            deliveryCount: 1);

        ServiceBusEnvelopeMapper.ToDeliveryInfo(received).IsReplay.ShouldBeTrue();
    }
}
