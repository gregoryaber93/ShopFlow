using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShopFlow.BuildingBlocks.Tests.Messaging;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Inbox;
using ShopFlow.Messaging.Outbox;
using ShopFlow.Persistence.Cosmos;
using ShopFlow.Persistence.Cosmos.Documents;
using ShopFlow.SharedKernel.Persistence;

namespace ShopFlow.BuildingBlocks.Tests.Cosmos;

/// <summary>
/// Assembling a TransactionalBatch and translating its errors. The Cosmos SDK is designed for mocking
/// (virtual members, protected constructors), so no emulator is needed.
/// </summary>
public sealed class CosmosUnitOfWorkTests : IDisposable
{
    private const string OrderId = "0193a1b2-0000-7000-8000-00000000000a";

    private readonly ServiceProvider _provider;
    private readonly IServiceScope _scope;
    private readonly Container _container = Substitute.For<Container>();
    private readonly TransactionalBatch _batch = Substitute.For<TransactionalBatch>();
    private readonly List<string> _operations = [];

    public CosmosUnitOfWorkTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cosmos:AccountEndpoint"] = "https://localhost:8081/",
                ["Cosmos:DatabaseName"] = "orders",
            })
            .Build();

        // No broker: the unit of work only writes the outbox; a separate process does the publishing.
        _provider = MessagingTestHost.Build(services: services => services.AddShopFlowCosmos(configuration));
        _scope = _provider.CreateScope();

        _container.Id.Returns("orders");
        _container.CreateTransactionalBatch(Arg.Any<PartitionKey>()).Returns(_batch);

        _batch.CreateItem(Arg.Any<InboxDocument>(), Arg.Any<TransactionalBatchItemRequestOptions>())
            .Returns(_batch).AndDoes(call => _operations.Add($"create {call.Arg<InboxDocument>().Id}"));
        _batch.CreateItem(Arg.Any<OutboxDocument>(), Arg.Any<TransactionalBatchItemRequestOptions>())
            .Returns(_batch).AndDoes(call => _operations.Add($"create {call.Arg<OutboxDocument>().Id}"));
        _batch.ReplaceItem(Arg.Any<string>(), Arg.Any<TestDocument>(), Arg.Any<TransactionalBatchItemRequestOptions>())
            .Returns(_batch).AndDoes(call => _operations.Add($"replace {call.ArgAt<string>(0)} if-match {call.Arg<TransactionalBatchItemRequestOptions>().IfMatchEtag}"));
    }

    public void Dispose()
    {
        _scope.Dispose();
        _provider.Dispose();
    }

    [Fact]
    public async Task Inbox_goes_first_then_aggregate_then_outbox_in_one_batch()
    {
        var incomingId = SetIncomingMessage();
        _scope.ServiceProvider.GetRequiredService<IOutbox>().Enqueue(MessagingTestHost.SampleOrder());
        ReturnsSuccess(operationCount: 3);

        await CreateUnitOfWork()
            .Replace(new TestDocument { Id = OrderId }, "\"etag-1\"")
            .CommitAsync(TestContext.Current.CancellationToken);

        _operations.Count.ShouldBe(3);
        _operations[0].ShouldBe($"create inbox:orders-processors:{incomingId:D}");
        _operations[1].ShouldBe($"replace {OrderId} if-match \"etag-1\"");
        _operations[2].ShouldStartWith("create outbox:");
        _container.Received(1).CreateTransactionalBatch(new PartitionKey(OrderId));
    }

    [Fact]
    public async Task Outbox_document_lands_in_the_aggregate_partition()
    {
        OutboxDocument? written = null;
        _batch.CreateItem(Arg.Do<OutboxDocument>(document => written = document), Arg.Any<TransactionalBatchItemRequestOptions>()).Returns(_batch);
        _scope.ServiceProvider.GetRequiredService<IOutbox>().Enqueue(MessagingTestHost.SampleOrder());
        ReturnsSuccess(operationCount: 1);

        await CreateUnitOfWork().CommitAsync(TestContext.Current.CancellationToken);

        written.ShouldNotBeNull();
        written.AdditionalProperties["orderId"].GetString().ShouldBe(OrderId);
        written.Destination.ShouldBe("test-topic");
        written.TimeToLive.ShouldBe((int)TimeSpan.FromDays(7).TotalSeconds);
    }

    [Fact]
    public async Task Conflict_on_the_inbox_document_means_duplicate_message()
    {
        var incomingId = SetIncomingMessage();
        ReturnsFailure(HttpStatusCode.Conflict, HttpStatusCode.FailedDependency);

        var exception = await Should.ThrowAsync<DuplicateMessageException>(() => CreateUnitOfWork()
            .Replace(new TestDocument { Id = OrderId }, "\"etag-1\"")
            .CommitAsync(TestContext.Current.CancellationToken));

        exception.Entry.ShouldBe(new InboxEntry(incomingId, "orders-processors"));
    }

    [Fact]
    public async Task Precondition_failed_on_the_aggregate_means_concurrency_conflict()
    {
        ReturnsFailure(HttpStatusCode.PreconditionFailed);

        await Should.ThrowAsync<ConcurrencyConflictException>(() => CreateUnitOfWork()
            .Replace(new TestDocument { Id = OrderId }, "\"stale\"")
            .CommitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Throttled_batch_surfaces_status_for_the_classifier()
    {
        var response = Substitute.For<TransactionalBatchResponse>();
        response.IsSuccessStatusCode.Returns(false);
        response.StatusCode.Returns(HttpStatusCode.TooManyRequests);
        response.Count.Returns(0);
        _batch.ExecuteAsync(Arg.Any<CancellationToken>()).Returns(response);

        var exception = await Should.ThrowAsync<CosmosBatchException>(() => CreateUnitOfWork()
            .Replace(new TestDocument { Id = OrderId }, "\"etag\"")
            .CommitAsync(TestContext.Current.CancellationToken));

        exception.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Nothing_to_write_means_no_round_trip()
    {
        var result = await CreateUnitOfWork().CommitAsync(TestContext.Current.CancellationToken);

        result.ShouldBeSameAs(CosmosCommitResult.Empty);
        _container.DidNotReceiveWithAnyArgs().CreateTransactionalBatch(default);
    }

    private CosmosUnitOfWork CreateUnitOfWork() =>
        _scope.ServiceProvider.GetRequiredService<ICosmosUnitOfWorkFactory>().Create(_container, "orderId", OrderId);

    private Guid SetIncomingMessage()
    {
        var id = Guid.CreateVersion7();
        _scope.ServiceProvider.GetRequiredService<MessageContextAccessor>().SetIncoming(new MessageContext
        {
            MessageId = id,
            MessageType = "ShipmentDispatched",
            ContractVersion = 1,
            CorrelationId = "c",
            Source = "shipping",
            OccurredAt = MessagingTestHost.Now,
            Consumer = "orders-processors",
            DeliveryCount = 1,
            EnqueuedAt = MessagingTestHost.Now,
        });
        return id;
    }

    private void ReturnsSuccess(int operationCount)
    {
        var response = Substitute.For<TransactionalBatchResponse>();
        response.IsSuccessStatusCode.Returns(true);
        response.Count.Returns(operationCount);
        response.RequestCharge.Returns(12.5);
        for (var index = 0; index < operationCount; index++)
        {
            var result = Substitute.For<TransactionalBatchOperationResult>();
            result.ETag.Returns($"\"etag-{index}\"");
            response[index].Returns(result);
        }

        _batch.ExecuteAsync(Arg.Any<CancellationToken>()).Returns(response);
    }

    private void ReturnsFailure(params HttpStatusCode[] statuses)
    {
        var response = Substitute.For<TransactionalBatchResponse>();
        response.IsSuccessStatusCode.Returns(false);
        response.StatusCode.Returns(statuses[0]);
        response.Count.Returns(statuses.Length);
        for (var index = 0; index < statuses.Length; index++)
        {
            var result = Substitute.For<TransactionalBatchOperationResult>();
            result.StatusCode.Returns(statuses[index]);
            result.IsSuccessStatusCode.Returns(false);
            response[index].Returns(result);
        }

        _batch.ExecuteAsync(Arg.Any<CancellationToken>()).Returns(response);
    }

    public sealed class TestDocument : CosmosDocument
    {
        public override string DocType => "test";
    }
}
