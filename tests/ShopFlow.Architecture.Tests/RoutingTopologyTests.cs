using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShopFlow.Contracts;
using ShopFlow.Inventory.Infrastructure;
using ShopFlow.Messaging.Routing;
using ShopFlow.Orders.Infrastructure;
using ShopFlow.Payments.Infrastructure;
using ShopFlow.Shipping.Infrastructure;
using ShopFlow.Tests.Shared;

namespace ShopFlow.Architecture.Tests;

/// <summary>
/// Publishing routes registered in service code versus the topology file from which Bicep creates the topics.
/// </summary>
public sealed class RoutingTopologyTests
{
    private static readonly MessagingTopologyFile Topology = MessagingTopologyFile.Load();

    public static TheoryData<string> Publishers => [.. Topology.ServiceBus.Topics.Select(topic => topic.Publisher)];

    /// <remarks>
    /// WHY: both directions. An event listed on a topic whose publisher has no route will never be published (an exception on
    /// the first attempt). A route to a topic missing from the topology means an entity Bicep will not create
    /// (MessagingEntityNotFound in production).
    /// </remarks>
    [Theory]
    [MemberData(nameof(Publishers))]
    public void Routes_of_a_publisher_match_its_topic(string publisher)
    {
        var topic = Topology.ServiceBus.Topics.Single(definition => definition.Publisher == publisher);
        var routes = RoutesOf(publisher);
        var registry = MessageTypeRegistry.Default;

        routes.Values.Distinct().ShouldBe([topic.Name]);
        routes.Keys.Select(type => registry.GetContractId(type).Name).Distinct().ShouldBe(topic.Messages, ignoreOrder: true);
    }

    private static IReadOnlyDictionary<Type, string> RoutesOf(string publisher)
    {
        var services = new ServiceCollection();
        _ = publisher switch
        {
            MessagingTopology.Consumers.OrdersProcessors => services.AddOrdersMessaging(),
            MessagingTopology.Consumers.InventoryWorker => services.AddInventoryMessaging(),
            MessagingTopology.Consumers.Payments => services.AddPaymentsMessaging(),
            MessagingTopology.Consumers.Shipping => services.AddShippingMessaging(),
            _ => throw new InvalidOperationException($"No messaging registration is known for publisher '{publisher}'."),
        };

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<MessageRoutingOptions>>().Value;

        // The route table is internal (services register routes only through MessagingBuilder.Route), so the test reads it with
        // reflection instead of widening the library's public API for the test's sake.
        var routes = (Dictionary<Type, string>)typeof(MessageRoutingOptions)
            .GetProperty("Routes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(options)!;

        return routes;
    }
}
