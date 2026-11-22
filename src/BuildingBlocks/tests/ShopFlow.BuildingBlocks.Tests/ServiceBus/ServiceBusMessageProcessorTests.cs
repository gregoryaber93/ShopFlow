using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShopFlow.BuildingBlocks.Tests.Messaging;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.ServiceBus;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.BuildingBlocks.Tests.ServiceBus;

[Collection(RecordingHandlerCollection.Name)]
public sealed class ServiceBusMessageProcessorTests : IDisposable
{
    private static readonly ConsumerEndpoint Endpoint = new("orders-processors");

    private readonly ServiceProvider _provider;
    private readonly ServiceBusMessageProcessor _processor;
    private readonly IMessageSettlement _settlement = Substitute.For<IMessageSettlement>();

    public ServiceBusMessageProcessorTests()
    {
        RecordingHandler.Calls.Clear();
        RecordingHandler.Behavior = (_, _) => Task.CompletedTask;

        // Zero retry delay: the test checks the decision, not the passage of time.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ServiceBus:Processing:RedeliveryBaseDelay"] = "00:00:00",
                ["ServiceBus:Processing:RedeliveryMaxDelay"] = "00:00:00",
            })
            .Build();

        _provider = MessagingTestHost.Build(messaging => messaging
            .AddHandler<TestOrderPlaced, RecordingHandler>()
            .UseAzureServiceBus(configuration));
        _processor = _provider.GetRequiredService<ServiceBusMessageProcessor>();
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task Successfully_handled_message_is_completed()
    {
        await _processor.ProcessAsync(Received(), _settlement, Endpoint, TestContext.Current.CancellationToken);

        await _settlement.Received(1).CompleteAsync(Arg.Any<CancellationToken>());
        await _settlement.DidNotReceive().DeadLetterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Permanent_failure_dead_letters_immediately_with_a_reason()
    {
        RecordingHandler.Behavior = (_, _) => throw new DomainException("order.unknown", "No such order");

        await _processor.ProcessAsync(Received(), _settlement, Endpoint, TestContext.Current.CancellationToken);

        await _settlement.Received(1).DeadLetterAsync(
            DeadLetterReasons.BusinessRuleViolation,
            Arg.Is<string>(description => description.Contains("order.unknown")),
            Arg.Any<CancellationToken>());
        await _settlement.DidNotReceive().CompleteAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Transient_failure_is_rethrown_so_the_host_abandons_the_message()
    {
        RecordingHandler.Behavior = (_, _) => throw new TimeoutException("Cosmos timed out");

        await Should.ThrowAsync<TimeoutException>(
            () => _processor.ProcessAsync(Received(), _settlement, Endpoint, TestContext.Current.CancellationToken));

        _settlement.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Message_without_envelope_metadata_is_dead_lettered_without_calling_handlers()
    {
        var received = ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("{}"),
            messageId: "not-a-guid",
            subject: "TestOrderPlaced");

        await _processor.ProcessAsync(received, _settlement, Endpoint, TestContext.Current.CancellationToken);

        await _settlement.Received(1).DeadLetterAsync(DeadLetterReasons.MalformedEnvelope, Arg.Any<string>(), Arg.Any<CancellationToken>());
        RecordingHandler.Calls.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(4, 8)]
    [InlineData(10, 30)]
    public void Redelivery_delay_grows_exponentially_up_to_the_cap(int deliveryCount, int expectedSeconds)
    {
        var processor = new ServiceBusMessageProcessor(
            _provider.GetRequiredService<ShopFlow.Messaging.Handling.MessageDispatcher>(),
            _provider.GetRequiredService<MessageFailureClassifier>(),
            Microsoft.Extensions.Options.Options.Create(new ServiceBusProcessingOptions()),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ServiceBusMessageProcessor>.Instance);

        processor.GetRedeliveryDelay(deliveryCount).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));
    }

    private ServiceBusReceivedMessage Received()
    {
        var sent = ServiceBusEnvelopeMapper.ToServiceBusMessage(MessagingTestHost.Envelope(_provider, MessagingTestHost.SampleOrder()));

        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: sent.Body,
            messageId: sent.MessageId,
            sessionId: sent.SessionId,
            correlationId: sent.CorrelationId,
            subject: sent.Subject,
            properties: sent.ApplicationProperties,
            deliveryCount: 1,
            enqueuedTime: MessagingTestHost.Now);
    }
}
