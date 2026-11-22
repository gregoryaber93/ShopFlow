using System.Reflection;
using ShopFlow.Contracts;
using ShopFlow.Tests.Shared;

namespace ShopFlow.Contracts.Tests;

/// <summary>
/// Consistency of the three places that describe messaging: the <see cref="MessagingTopology"/> constants in code, the
/// topology file read by Bicep, and the contract catalog.
/// </summary>
/// <remarks>
/// WHY: a typo in a queue name or a subscription filter does not cause a compile error. At best it ends with
/// MessagingEntityNotFound at startup, at worst with silently lost messages: a filter on a type that does not exist simply
/// never lets anything through. These tests move such errors into CI.
/// </remarks>
public sealed class MessagingTopologyTests
{
    private static readonly MessagingTopologyFile Topology = MessagingTopologyFile.Load();

    private static readonly IReadOnlyList<(string Name, int Version, Type Type)> Contracts =
    [
        .. typeof(MessagingTopology).Assembly.GetTypes()
            .Select(type => (Type: type, Attribute: type.GetCustomAttribute<MessageContractAttribute>()))
            .Where(item => item.Attribute is not null)
            .Select(item => (item.Attribute!.Name, item.Attribute.Version, item.Type)),
    ];

    [Fact]
    public void Queues_in_code_and_in_bicep_are_the_same()
    {
        Topology.ServiceBus.Queues.Select(queue => queue.Name).ShouldBe(Constants(typeof(MessagingTopology.Queues)), ignoreOrder: true);
    }

    [Fact]
    public void Topics_in_code_and_in_bicep_are_the_same()
    {
        Topology.ServiceBus.Topics.Select(topic => topic.Name).ShouldBe(Constants(typeof(MessagingTopology.Topics)), ignoreOrder: true);
    }

    [Fact]
    public void Every_subscription_name_is_a_known_constant_and_every_constant_is_used()
    {
        var used = Topology.ServiceBus.Topics.SelectMany(topic => topic.Subscriptions).Select(subscription => subscription.Name).Distinct();

        used.ShouldBe(Constants(typeof(MessagingTopology.Subscriptions)), ignoreOrder: true);
    }

    [Fact]
    public void Event_hubs_and_consumer_groups_match_the_code()
    {
        Topology.EventHubs.Hubs.Select(hub => hub.Name).ShouldBe(Constants(typeof(MessagingTopology.EventHubs)), ignoreOrder: true);
        Topology.EventHubs.Hubs.SelectMany(hub => hub.ConsumerGroups).ShouldBe(Constants(typeof(MessagingTopology.EventHubs.ConsumerGroups)), ignoreOrder: true);
    }

    [Fact]
    public void Queue_consumers_are_known_consumer_names()
    {
        var consumers = Constants(typeof(MessagingTopology.Consumers));

        Topology.ServiceBus.Queues.ShouldAllBe(queue => consumers.Contains(queue.Consumer));
    }

    /// <remarks>
    /// WHY: from the consumer and senders fields Bicep grants the Data Receiver and Data Sender roles on individual entities.
    /// A directly read subscription without a named consumer would mean a function without permissions
    /// (UnauthorizedAccessException when the trigger starts), and a sender outside the list of services is a typo.
    /// </remarks>
    [Fact]
    public void Every_reader_and_sender_has_a_known_identity()
    {
        var consumers = Constants(typeof(MessagingTopology.Consumers));
        var directReads = Topology.ServiceBus.Topics.SelectMany(topic => topic.Subscriptions).Where(subscription => subscription.ForwardTo is null).ToList();

        directReads.ShouldNotBeEmpty();
        directReads.ShouldAllBe(subscription => subscription.Consumer != null && consumers.Contains(subscription.Consumer));
        Topology.ServiceBus.Queues.SelectMany(queue => queue.Senders ?? []).ShouldAllBe(sender => consumers.Contains(sender));
        Topology.ServiceBus.Topics.ShouldAllBe(topic => consumers.Contains(topic.Publisher));
    }

    /// <remarks>
    /// AZURE: a subscription filter compares strings. A filter on "OrderConfimred" (a typo) is a valid rule that never lets
    /// anything through, and Service Bus reports no error.
    /// </remarks>
    [Fact]
    public void Every_message_name_in_the_topology_is_a_real_contract()
    {
        var names = Topology.ServiceBus.Topics
            .SelectMany(topic => topic.Messages.Concat(topic.Subscriptions.SelectMany(subscription => subscription.AcceptedMessages(topic))))
            .Concat(Topology.ServiceBus.Queues.SelectMany(queue => queue.Messages))
            .Distinct();

        names.ShouldAllBe(name => Contracts.Any(contract => contract.Name == name));
    }

    [Fact]
    public void Filters_only_select_messages_that_the_topic_carries()
    {
        var invalid = Topology.ServiceBus.Topics
            .SelectMany(topic => topic.Subscriptions.SelectMany(subscription => subscription.AcceptedMessages(topic)
                .Where(name => !topic.Messages.Contains(name))
                .Select(name => $"{topic.Name}/{subscription.Name}: {name}")))
            .ToList();

        invalid.ShouldBeEmpty();
    }

    [Fact]
    public void Forwarding_targets_are_existing_queues()
    {
        var queues = Topology.ServiceBus.Queues.Select(queue => queue.Name).ToHashSet();

        Topology.ServiceBus.Topics
            .SelectMany(topic => topic.Subscriptions)
            .Where(subscription => subscription.ForwardTo is not null)
            .ShouldAllBe(subscription => queues.Contains(subscription.ForwardTo!));
    }

    /// <remarks>
    /// AZURE: only a message with a SessionId can be sent (including by auto-forwarding) to a session-enabled queue.
    /// ShopFlow sets SessionId = orderId for <see cref="IOrderScoped"/> contracts. If an unfiltered subscription forwarded an
    /// event without an order (e.g. StockLevelLow) to a session-enabled queue, the forward would fail and the message would
    /// land in the subscription's DLQ, where nobody looks for it.
    /// </remarks>
    [Fact]
    public void Only_order_scoped_messages_can_reach_a_session_queue()
    {
        var sessionQueues = Topology.ServiceBus.Queues.Where(queue => queue.RequiresSession).ToDictionary(queue => queue.Name);

        var reaching = sessionQueues.Values
            .SelectMany(queue => queue.Messages.Select(name => (Queue: queue.Name, Name: name)))
            .Concat(Topology.ServiceBus.Topics.SelectMany(topic => topic.Subscriptions
                .Where(subscription => subscription.ForwardTo is { } target && sessionQueues.ContainsKey(target))
                .SelectMany(subscription => subscription.AcceptedMessages(topic).Select(name => (Queue: subscription.ForwardTo!, Name: name)))));

        var violations = reaching
            .SelectMany(item => Contracts.Where(contract => contract.Name == item.Name).Select(contract => (item.Queue, contract)))
            .Where(item => !typeof(IOrderScoped).IsAssignableFrom(item.contract.Type))
            .Select(item => $"{item.contract.Name} v{item.contract.Version} → {item.Queue}")
            .ToList();

        violations.ShouldBeEmpty();
    }

    /// <summary>Every contract has a path to a consumer; a contract nobody sends is dead code in the package.</summary>
    [Fact]
    public void Every_contract_is_carried_by_a_topic_or_a_queue()
    {
        var carried = Topology.ServiceBus.Topics.SelectMany(topic => topic.Messages)
            .Concat(Topology.ServiceBus.Queues.SelectMany(queue => queue.Messages))
            .ToHashSet();

        Contracts.Select(contract => contract.Name).Distinct().Where(name => !carried.Contains(name)).ShouldBeEmpty();
    }

    [Fact]
    public void Every_event_is_published_on_exactly_one_topic()
    {
        Topology.ServiceBus.Topics
            .SelectMany(topic => topic.Messages.Select(name => (Topic: topic.Name, Name: name)))
            .GroupBy(item => item.Name)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ShouldBeEmpty();
    }

    private static List<string> Constants(Type holder) =>
    [
        .. holder.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!),
    ];
}
