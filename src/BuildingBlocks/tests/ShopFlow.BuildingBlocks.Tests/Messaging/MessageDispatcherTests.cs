using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.Handling;
using ShopFlow.Messaging.Inbox;

namespace ShopFlow.BuildingBlocks.Tests.Messaging;

[Collection(RecordingHandlerCollection.Name)]
public sealed class MessageDispatcherTests : IDisposable
{
    private static readonly ConsumerEndpoint StrictEndpoint = new("strict-consumer");
    private static readonly ConsumerEndpoint LenientEndpoint = new("lenient-consumer", UnhandledMessagePolicy.Ignore);
    private static readonly DeliveryInfo FirstDelivery = new(1, MessagingTestHost.Now);

    private readonly ServiceProvider _provider;
    private readonly MessageDispatcher _dispatcher;

    public MessageDispatcherTests()
    {
        RecordingHandler.Calls.Clear();
        RecordingHandler.Behavior = (_, _) => Task.CompletedTask;

        _provider = MessagingTestHost.Build(messaging => messaging.AddHandler<TestOrderPlaced, RecordingHandler>());
        _dispatcher = _provider.GetRequiredService<MessageDispatcher>();
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task Known_message_is_deserialized_and_handled_with_its_context()
    {
        var message = MessagingTestHost.SampleOrder("first");
        var envelope = MessagingTestHost.Envelope(_provider, message, correlationId: "flow-7");

        var outcome = await _dispatcher.DispatchAsync(envelope, StrictEndpoint, FirstDelivery, TestContext.Current.CancellationToken);

        outcome.ShouldBe(DispatchOutcome.Handled);
        var call = RecordingHandler.Calls.ShouldHaveSingleItem();
        call.Message.ShouldBe(message);
        call.Context.Consumer.ShouldBe("strict-consumer");
        call.Context.InboxEntry.ShouldBe(new InboxEntry(envelope.MessageId, "strict-consumer"));

        // The accessor in the handler's scope sees the same message: the outbox and the unit of work use it.
        call.CorrelationFromAccessor.ShouldBe("flow-7");
    }

    [Fact]
    public async Task Message_without_handler_goes_to_dlq_on_a_strict_endpoint()
    {
        var envelope = MessagingTestHost.Envelope(_provider, new TestPing { Text = "x" });

        var exception = await Should.ThrowAsync<PermanentMessageException>(
            () => _dispatcher.DispatchAsync(envelope, StrictEndpoint, FirstDelivery, TestContext.Current.CancellationToken));

        exception.Reason.ShouldBe(DeadLetterReasons.UnhandledMessageType);
    }

    [Fact]
    public async Task Message_without_handler_is_ignored_on_a_lenient_endpoint()
    {
        var envelope = MessagingTestHost.Envelope(_provider, new TestPing { Text = "x" });

        var outcome = await _dispatcher.DispatchAsync(envelope, LenientEndpoint, FirstDelivery, TestContext.Current.CancellationToken);

        outcome.ShouldBe(DispatchOutcome.Ignored);
    }

    [Fact]
    public async Task Unknown_contract_version_is_treated_like_a_missing_handler()
    {
        var envelope = MessagingTestHost.Envelope(_provider, MessagingTestHost.SampleOrder()) with { ContractVersion = 99 };

        await Should.ThrowAsync<PermanentMessageException>(
            () => _dispatcher.DispatchAsync(envelope, StrictEndpoint, FirstDelivery, TestContext.Current.CancellationToken));
        RecordingHandler.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Body_that_does_not_match_the_contract_is_a_permanent_failure()
    {
        var envelope = MessagingTestHost.Envelope(_provider, MessagingTestHost.SampleOrder()) with
        {
            Payload = Encoding.UTF8.GetBytes("""{ "orderId": "not-a-guid" }"""),
        };

        var exception = await Should.ThrowAsync<PermanentMessageException>(
            () => _dispatcher.DispatchAsync(envelope, StrictEndpoint, FirstDelivery, TestContext.Current.CancellationToken));

        exception.Reason.ShouldBe(DeadLetterReasons.DeserializationFailed);
    }

    [Fact]
    public async Task Duplicate_detected_by_the_inbox_is_reported_as_success()
    {
        RecordingHandler.Behavior = (_, context) => throw new DuplicateMessageException(context.InboxEntry);
        var envelope = MessagingTestHost.Envelope(_provider, MessagingTestHost.SampleOrder());

        var outcome = await _dispatcher.DispatchAsync(envelope, StrictEndpoint, new DeliveryInfo(2, MessagingTestHost.Now), TestContext.Current.CancellationToken);

        outcome.ShouldBe(DispatchOutcome.Duplicate);
    }

    [Fact]
    public async Task Handler_exception_propagates_for_classification()
    {
        RecordingHandler.Behavior = (_, _) => throw new TimeoutException("db");
        var envelope = MessagingTestHost.Envelope(_provider, MessagingTestHost.SampleOrder());

        await Should.ThrowAsync<TimeoutException>(
            () => _dispatcher.DispatchAsync(envelope, StrictEndpoint, FirstDelivery, TestContext.Current.CancellationToken));
    }
}
