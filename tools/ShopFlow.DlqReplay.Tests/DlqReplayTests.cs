using System.Text.Json;
using Azure.Messaging.ServiceBus;
using ShopFlow.Contracts;

namespace ShopFlow.DlqReplay.Tests;

public sealed class ReplayPlannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly ReplayTarget Queue = new("notifications-inbox", null);
    private static readonly ReplayFilter All = new(new HashSet<string>(), null, Force: false);

    [Fact]
    public void Replayed_copy_keeps_identity_and_session_and_is_marked_as_a_replay()
    {
        var message = DeadLettered(replayCount: 1);

        var replay = ReplayPlanner.Plan(message, Queue, All, Now).ShouldBeOfType<ReplayDecision.Replay>().Message;

        replay.MessageId.ShouldBe(message.MessageId);
        replay.SessionId.ShouldBe(message.SessionId);
        replay.Subject.ShouldBe("OrderConfirmed");
        replay.ApplicationProperties[MessageHeaders.ReplayedFromDeadLetter].ShouldBe(true);
        replay.ApplicationProperties[MessageHeaders.ReplayCount].ShouldBe(2);
        replay.ApplicationProperties["LastDeadLetterReason"].ShouldBe("MaxDeliveryCountExceeded");
        replay.ApplicationProperties.ShouldNotContainKey("DeadLetterReason");
        replay.ApplicationProperties.ShouldNotContainKey(ReplayPlanner.ReplayTargetProperty);
    }

    [Fact]
    public void Poison_message_is_not_replayed_a_fourth_time_unless_forced()
    {
        var message = DeadLettered(replayCount: ReplayPlanner.MaxReplays);

        ReplayPlanner.Plan(message, Queue, All, Now).ShouldBe(new ReplayDecision.Skip("replay_limit_reached"));
        ReplayPlanner.Plan(message, Queue, All with { Force = true }, Now).ShouldBeOfType<ReplayDecision.Replay>();
    }

    [Fact]
    public void Message_inside_the_duplicate_detection_window_would_be_dropped_by_the_broker_so_it_is_skipped()
    {
        var message = DeadLettered(enqueuedAt: Now.AddMinutes(-3));

        ReplayPlanner.Plan(message, Queue, All, Now).ShouldBe(new ReplayDecision.Skip("inside_duplicate_detection_window"));
    }

    [Fact]
    public void Only_selected_messages_or_reasons_are_replayed()
    {
        var message = DeadLettered();

        ReplayPlanner.Plan(message, Queue, All with { MessageIds = new HashSet<string> { "other" } }, Now).ShouldBe(new ReplayDecision.Skip("not_selected"));
        ReplayPlanner.Plan(message, Queue, All with { Reason = "DeserializationFailed" }, Now).ShouldBe(new ReplayDecision.Skip("other_reason"));
        ReplayPlanner.Plan(message, Queue, All with { Reason = "MaxDeliveryCountExceeded" }, Now).ShouldBeOfType<ReplayDecision.Replay>();
    }

    [Fact]
    public void Replay_through_a_topic_is_addressed_to_one_subscription()
    {
        var replay = ReplayPlanner.Plan(DeadLettered(), new ReplayTarget("orders-events", "saga-start"), All, Now)
            .ShouldBeOfType<ReplayDecision.Replay>().Message;

        replay.ApplicationProperties[ReplayPlanner.ReplayTargetProperty].ShouldBe("saga-start");
    }

    private static ServiceBusReceivedMessage DeadLettered(int replayCount = 0, DateTimeOffset? enqueuedAt = null)
    {
        var properties = new Dictionary<string, object>
        {
            ["DeadLetterReason"] = "MaxDeliveryCountExceeded",
            ["DeadLetterErrorDescription"] = "Message could not be consumed after 10 delivery attempts.",
            [MessageHeaders.ContractVersion] = "1",
        };

        if (replayCount > 0)
        {
            properties[MessageHeaders.ReplayCount] = replayCount;
        }

        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("""{"orderId":"0193a1b2-0000-7000-8000-000000000001"}"""),
            messageId: "0193a1b2-0000-7000-8000-0000000000ff",
            sessionId: "0193a1b2-0000-7000-8000-000000000001",
            subject: "OrderConfirmed",
            properties: properties,
            deliveryCount: 10,
            enqueuedTime: enqueuedAt ?? Now.AddHours(-2));
    }
}

public sealed class ReplayTargetResolverTests
{
    private static readonly ReplayTargetResolver Resolver = ReplayTargetResolver.FromEmbeddedTopology();

    [Theory]
    [InlineData(null, "orders-events", "notifications", "notifications-inbox", null)]
    [InlineData(null, "orders-events", "saga-start", "orders-events", "saga-start")]
    [InlineData(null, "payments-events", "invoicing", "payments-events", "invoicing")]
    [InlineData("payments-commands", null, null, "payments-commands", null)]
    public void Target_follows_the_topology(string? queue, string? topic, string? subscription, string entity, string? replayTarget)
    {
        Resolver.Resolve(new DeadLetterSource(queue, topic, subscription)).ShouldBe(new ReplayTarget(entity, replayTarget));
    }

    [Fact]
    public void Unknown_entity_is_rejected_instead_of_guessed()
    {
        Should.Throw<ArgumentException>(() => Resolver.Resolve(new DeadLetterSource("notificatons-inbox", null, null)));
        Should.Throw<ArgumentException>(() => Resolver.Resolve(new DeadLetterSource(null, "orders-events", "nope")));
    }

    [Fact]
    public void Replay_property_name_matches_the_topology_used_by_bicep()
    {
        using var stream = typeof(ReplayTargetResolver).Assembly.GetManifestResourceStream("messaging-topology.json")!;
        using var topology = JsonDocument.Parse(stream);

        topology.RootElement.GetProperty("replay").GetProperty("property").GetString().ShouldBe(ReplayPlanner.ReplayTargetProperty);
    }
}

public sealed class CommandLineTests
{
    [Fact]
    public void Replay_options_are_parsed_and_the_namespace_can_come_from_the_environment()
    {
        var arguments = CommandLine.Parse(
            ["replay", "--topic", "orders-events", "--subscription", "saga-start", "--message-id", "a", "--message-id", "b", "--dry-run"],
            name => name == "SHOPFLOW_SERVICEBUS_NAMESPACE" ? "sbns-shopflow-dev-weu.servicebus.windows.net" : null);

        arguments.Command.ShouldBe(DlqCommand.Replay);
        arguments.Namespace.ShouldBe("sbns-shopflow-dev-weu.servicebus.windows.net");
        arguments.Source.ShouldBe(new DeadLetterSource(null, "orders-events", "saga-start"));
        arguments.Filter.MessageIds.ShouldBe(["a", "b"], ignoreOrder: true);
        arguments.DryRun.ShouldBeTrue();
        arguments.Max.ShouldBe(50);
    }

    [Theory]
    [InlineData("peek", "--queue", "q")]
    [InlineData("peek", "--namespace", "ns", "--queue", "q", "--topic", "t")]
    [InlineData("peek", "--namespace", "ns", "--queue", "q", "--force")]
    [InlineData("purge", "--namespace", "ns", "--queue", "q")]
    public void Ambiguous_or_dangerous_invocations_are_refused(params string[] args) =>
        Should.Throw<ArgumentException>(() => CommandLine.Parse(args, _ => null));
}
