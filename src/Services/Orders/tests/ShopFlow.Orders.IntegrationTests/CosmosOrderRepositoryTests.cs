using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Inbox;
using ShopFlow.Orders.Application;
using ShopFlow.Orders.Application.Abstractions;
using ShopFlow.Orders.Application.Orders;
using ShopFlow.Orders.Domain.Orders;
using ShopFlow.Orders.Infrastructure;
using ShopFlow.Orders.Infrastructure.Cosmos;
using ShopFlow.Orders.Testing;
using ShopFlow.SharedKernel.Domain;
using ShopFlow.SharedKernel.Persistence;

namespace ShopFlow.Orders.IntegrationTests;

/// <summary>The real CosmosOrderRepository and CosmosUnitOfWork against the emulator.</summary>
public sealed class CosmosOrderRepositoryTests(CosmosEmulatorFixture fixture) : IClassFixture<CosmosEmulatorFixture>
{
    private const string SkipReason = "Integration tests need Docker; set SHOPFLOW_RUN_INTEGRATION_TESTS=1 to run them.";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Order_and_its_outbox_records_land_together_in_the_order_partition()
    {
        Assert.SkipUnless(CosmosEmulatorFixture.Enabled, SkipReason);
        using var provider = BuildServices();
        using var scope = provider.CreateScope();

        var order = NewOrder();
        await scope.ServiceProvider.GetRequiredService<OrderEventPublisher>().PublishAsync(order);
        await scope.ServiceProvider.GetRequiredService<IOrderRepository>().AddAsync(order, Ct);

        var docTypes = await QueryPartitionAsync<string>("SELECT VALUE c.docType FROM c", order.Id);
        docTypes.Order(StringComparer.Ordinal).ShouldBe(["order", "outbox", "outbox"]);
    }

    [Fact]
    public async Task Update_with_a_stale_etag_is_rejected_and_writes_no_outbox_record()
    {
        Assert.SkipUnless(CosmosEmulatorFixture.Enabled, SkipReason);
        using var provider = BuildServices();
        var orderId = await GivenStoredOrderAsync(provider);

        using var scope = provider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var order = (await repository.GetAsync(orderId, Ct))!;
        order.SetConcurrencyToken("\"00000000-0000-0000-0000-000000000000\"");
        order.Confirm(null, DateTimeOffset.UtcNow);
        await scope.ServiceProvider.GetRequiredService<OrderEventPublisher>().PublishAsync(order);

        await Should.ThrowAsync<ConcurrencyConflictException>(() => repository.UpdateAsync(order, Ct));

        // TransactionalBatch is atomic: the rejected Replace also rolled back the Create of the outbox record.
        var outbox = await QueryPartitionAsync<int>("SELECT VALUE COUNT(1) FROM c WHERE c.docType = 'outbox'", orderId);
        outbox.Single().ShouldBe(2);
    }

    [Fact]
    public async Task The_same_message_cannot_change_the_order_twice()
    {
        Assert.SkipUnless(CosmosEmulatorFixture.Enabled, SkipReason);
        using var provider = BuildServices();
        var orderId = await GivenStoredOrderAsync(provider);
        var messageId = Guid.CreateVersion7();

        await ChangeWithinMessageAsync(provider, orderId, messageId, order => order.Confirm(null, DateTimeOffset.UtcNow));

        // A second handling of the same message (e.g. after a lost lock) with a different change: the inbox rejects it.
        await Should.ThrowAsync<DuplicateMessageException>(() =>
            ChangeWithinMessageAsync(provider, orderId, messageId, order => order.MarkShipped("TRK", DateTimeOffset.UtcNow)));
    }

    private ServiceProvider BuildServices()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cosmos:AccountEndpoint"] = "https://localhost:8081/",
                ["Cosmos:DatabaseName"] = OrdersContainers.DatabaseName,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(fixture.Client);
        services.AddOrdersMessaging();
        services.AddOrdersApplication();
        services.AddOrdersInfrastructure(configuration);
        services.AddSingleton<IOrderFeatures>(new FakeOrderFeatures());
        return services.BuildServiceProvider();
    }

    private static Order NewOrder()
    {
        var address = new ShippingAddress("Jan", "ul. Prosta 1", null, "Warszawa", "00-001", "PL", null);
        return Order.Submit(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            "SF-261121-INTEG001",
            [new OrderLine("SKU-1", "Kubek", 1, new Money(10m, "PLN"))],
            Delivery.Courier(address),
            "pm",
            DateTimeOffset.UtcNow).Value;
    }

    private static async Task<Guid> GivenStoredOrderAsync(IServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var order = NewOrder();
        await scope.ServiceProvider.GetRequiredService<OrderEventPublisher>().PublishAsync(order);
        await scope.ServiceProvider.GetRequiredService<IOrderRepository>().AddAsync(order, Ct);
        return order.Id;
    }

    private static async Task ChangeWithinMessageAsync(IServiceProvider provider, Guid orderId, Guid messageId, Func<Order, ShopFlow.SharedKernel.Results.Result> change)
    {
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MessageContextAccessor>().SetIncoming(new MessageContext
        {
            MessageId = messageId,
            MessageType = "Test",
            ContractVersion = 1,
            CorrelationId = "it",
            Source = "test",
            OccurredAt = DateTimeOffset.UtcNow,
            Consumer = "orders-processors",
            DeliveryCount = 1,
            EnqueuedAt = DateTimeOffset.UtcNow,
        });

        var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var order = (await repository.GetAsync(orderId, Ct))!;
        change(order).IsSuccess.ShouldBeTrue();
        await scope.ServiceProvider.GetRequiredService<OrderEventPublisher>().PublishAsync(order);
        await repository.UpdateAsync(order, Ct);
    }

    private async Task<List<T>> QueryPartitionAsync<T>(string query, Guid orderId)
    {
        var container = fixture.Client.GetContainer(OrdersContainers.DatabaseName, OrdersContainers.OrdersName);
        using var iterator = container.GetItemQueryIterator<T>(
            new QueryDefinition(query),
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(orderId.ToString("D")) });

        var results = new List<T>();
        while (iterator.HasMoreResults)
        {
            results.AddRange(await iterator.ReadNextAsync(Ct));
        }

        return results;
    }
}
