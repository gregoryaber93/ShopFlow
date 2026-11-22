using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Outbox;

namespace ShopFlow.BuildingBlocks.Tests.Messaging;

public sealed class MessageEnvelopeFactoryTests
{
    [Fact]
    public void Order_scoped_message_gets_order_id_as_session_id()
    {
        using var provider = MessagingTestHost.Build();
        var message = MessagingTestHost.SampleOrder();

        var envelope = MessagingTestHost.Envelope(provider, message);

        envelope.SessionId.ShouldBe(message.OrderId.ToString("D"));
        envelope.MessageType.ShouldBe("TestOrderPlaced");
        envelope.ContractVersion.ShouldBe(1);
        envelope.Source.ShouldBe("test-service");
        envelope.OccurredAt.ShouldBe(MessagingTestHost.Now);
    }

    [Fact]
    public void Message_without_order_scope_has_no_session()
    {
        using var provider = MessagingTestHost.Build();

        var envelope = MessagingTestHost.Envelope(provider, new TestPing { Text = "x" });

        envelope.SessionId.ShouldBeNull();
    }

    [Fact]
    public void Payload_is_camel_case_json_of_the_contract()
    {
        using var provider = MessagingTestHost.Build();

        var envelope = MessagingTestHost.Envelope(provider, MessagingTestHost.SampleOrder("abc"));

        using var json = JsonDocument.Parse(envelope.Payload);
        json.RootElement.GetProperty("note").GetString().ShouldBe("abc");
    }

    [Fact]
    public void Current_trace_context_is_captured_for_the_relay()
    {
        using var provider = MessagingTestHost.Build();
        using var activity = new Activity("checkout").SetIdFormat(ActivityIdFormat.W3C).Start();

        var envelope = MessagingTestHost.Envelope(provider, MessagingTestHost.SampleOrder());

        envelope.TraceParent.ShouldBe(activity.Id);
    }

    [Fact]
    public void Explicit_message_id_is_kept_for_deterministic_sends()
    {
        using var provider = MessagingTestHost.Build();
        var id = DeterministicGuid.Create("order-1/ReserveStock/1");

        var envelope = provider.GetRequiredService<MessageEnvelopeFactory>()
            .Create(new TestPing { Text = "x" }, new MessageMetadata("c"), id);

        envelope.MessageId.ShouldBe(id);
    }

    [Fact]
    public void Outbox_inherits_correlation_and_causation_from_the_incoming_message()
    {
        using var provider = MessagingTestHost.Build();
        using var scope = provider.CreateScope();

        var incomingId = Guid.NewGuid();
        scope.ServiceProvider.GetRequiredService<MessageContextAccessor>().SetIncoming(new MessageContext
        {
            MessageId = incomingId,
            MessageType = "Something",
            ContractVersion = 1,
            CorrelationId = "order-flow-42",
            Source = "upstream",
            OccurredAt = MessagingTestHost.Now,
            Consumer = "test",
            DeliveryCount = 1,
            EnqueuedAt = MessagingTestHost.Now,
        });

        scope.ServiceProvider.GetRequiredService<IOutbox>().Enqueue(new TestPing { Text = "reply" });
        var pending = scope.ServiceProvider.GetRequiredService<IPendingOutbox>().Drain();

        var outbound = pending.ShouldHaveSingleItem();
        outbound.Destination.ShouldBe("test-queue");
        outbound.Envelope.CorrelationId.ShouldBe("order-flow-42");
        outbound.Envelope.CausationId.ShouldBe(incomingId.ToString("D"));
    }

    [Fact]
    public void Enqueue_of_unrouted_message_fails_in_business_code_not_in_the_relay()
    {
        using var provider = MessagingTestHost.Build();
        using var scope = provider.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();

        Should.Throw<InvalidOperationException>(() => outbox.Enqueue(new UnroutedMessage()));
    }

    [ShopFlow.Contracts.MessageContract("UnroutedMessage", 1)]
    private sealed record UnroutedMessage : ShopFlow.Contracts.ICommand;
}
