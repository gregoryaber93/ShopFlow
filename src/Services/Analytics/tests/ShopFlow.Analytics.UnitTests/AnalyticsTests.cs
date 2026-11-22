using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using ShopFlow.Analytics.Application;
using ShopFlow.Analytics.Infrastructure;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Analytics.V1;
using ShopFlow.Contracts.Common;
using ShopFlow.Messaging;
using PaymentsV1 = ShopFlow.Contracts.Payments.V1;

namespace ShopFlow.Analytics.UnitTests;

public sealed class ClickstreamIngestorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly RecordingPublisher _publisher = new();
    private readonly ClickstreamIngestor _ingestor;

    public ClickstreamIngestorTests() => _ingestor = new ClickstreamIngestor(_publisher, new FakeTimeProvider(Now));

    [Fact]
    public async Task Customer_id_comes_from_the_token_never_from_the_body()
    {
        var authenticated = Guid.NewGuid();

        await _ingestor.IngestAsync(Batch(Valid()), authenticated, CancellationToken.None);
        await _ingestor.IngestAsync(Batch(Valid()), authenticatedCustomerId: null, CancellationToken.None);

        _publisher.Published[0].CustomerId.ShouldBe(authenticated);
        _publisher.Published[1].CustomerId.ShouldBeNull();
    }

    [Fact]
    public async Task Bad_events_are_rejected_one_by_one_and_the_rest_is_published()
    {
        var result = await _ingestor.IngestAsync(
            Batch(
                Valid(),
                Valid() with { EventType = "purchase_completed" },
                Valid() with { SessionId = "x" },
                Valid() with { OccurredAt = Now.AddHours(-2) },
                Valid() with { EventId = null }),
            null,
            CancellationToken.None);

        result.Accepted.ShouldBe(1);
        result.Rejected.Select(rejected => rejected.Reason).ShouldBe(["event_type_unknown", "session_id_invalid", "too_old", "event_id_missing"]);
        _publisher.Published.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Page_url_keeps_only_campaign_parameters_and_referrer_only_the_host()
    {
        var input = Valid() with
        {
            PageUrl = "https://shopflow.example/p/mug?email=jan@example.com&utm_source=newsletter&reset_token=abc",
            Referrer = "https://mail.example.com/inbox/123?session=secret",
        };

        await _ingestor.IngestAsync(Batch(input), null, CancellationToken.None);

        var published = _publisher.Published.ShouldHaveSingleItem();
        published.PageUrl.ShouldBe("https://shopflow.example/p/mug?utm_source=newsletter");
        published.Referrer.ShouldBe("mail.example.com");
    }

    [Fact]
    public async Task Timestamp_from_the_future_is_replaced_with_the_time_of_receipt()
    {
        await _ingestor.IngestAsync(Batch(Valid() with { OccurredAt = Now.AddHours(3) }), null, CancellationToken.None);

        _publisher.Published.ShouldHaveSingleItem().OccurredAt.ShouldBe(Now);
    }

    [Fact]
    public async Task Oversized_request_is_refused_as_a_whole() =>
        await Should.ThrowAsync<ArgumentException>(() =>
            _ingestor.IngestAsync(Batch([.. Enumerable.Range(0, 101).Select(_ => Valid())]), null, CancellationToken.None));

    private static ClickstreamEventInput Valid() => new(
        Guid.NewGuid(),
        "sess-1234567890",
        ClickstreamEventTypes.ProductView,
        "SKU-1",
        "https://shopflow.example/p/mug",
        null,
        Now.AddSeconds(-3),
        null);

    private static ClickstreamBatchRequest Batch(params ClickstreamEventInput[] events) => new(events);

    private sealed class RecordingPublisher : IClickstreamPublisher
    {
        public List<ClickstreamEvent> Published { get; } = [];

        public Task PublishAsync(IReadOnlyList<ClickstreamEvent> events, CancellationToken cancellationToken)
        {
            Published.AddRange(events);
            return Task.CompletedTask;
        }
    }
}

public sealed class BusinessEventTests
{
    private static readonly Guid OrderId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000a9");
    private static readonly Guid CustomerId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000b9");

    [Fact]
    public void Envelope_is_forwarded_with_the_original_json_and_ids_read_without_contract_types()
    {
        var businessEvent = BusinessEventMapper.FromEnvelope(Envelope(Captured(149.98m)));

        businessEvent.EventType.ShouldBe("PaymentCaptured");
        businessEvent.OrderId.ShouldBe(OrderId);
        businessEvent.CustomerId.ShouldBe(CustomerId);
        businessEvent.Payload.GetProperty("providerReference").GetString().ShouldBe("psp-1");
        BusinessEventMapper.PartitionKey(businessEvent).ShouldBe(OrderId.ToString("D"));
    }

    [Fact]
    public void Captured_payment_becomes_revenue_in_minor_units()
    {
        var increment = BusinessMetricsCalculator.Calculate(BusinessEventMapper.FromEnvelope(Envelope(Captured(149.98m))))!;

        increment.Window.Id.ShouldBe("business:2026-11-22T12:34");
        increment.Counters.ShouldBe(new Dictionary<string, long> { ["revenueMinor.PLN"] = 14998 });
    }

    [Fact]
    public void Order_submitted_is_counted_only_in_version_2()
    {
        BusinessMetricsCalculator.Calculate(Event("OrderSubmitted", 1, """{"orderId":"0193a1b2-0000-7000-8000-0000000000a9"}""")).ShouldBeNull();
        BusinessMetricsCalculator.Calculate(Event("OrderSubmitted", 2, """{"orderId":"0193a1b2-0000-7000-8000-0000000000a9"}"""))!
            .Counters.ShouldBe(new Dictionary<string, long> { ["ordersSubmitted"] = 1 });
    }

    [Fact]
    public void Zero_refund_and_unrelated_events_do_not_touch_the_metrics()
    {
        BusinessMetricsCalculator.Calculate(Event("PaymentRefunded", 1, """{"amount":{"amount":0,"currency":"PLN"}}""")).ShouldBeNull();
        BusinessMetricsCalculator.Calculate(Event("ShipmentCreated", 1, "{}")).ShouldBeNull();
    }

    [Fact]
    public void Rejection_reason_becomes_its_own_counter()
    {
        BusinessMetricsCalculator.Calculate(Event("OrderRejected", 1, """{"reasonCode":"out_of_stock"}"""))!
            .Counters.ShouldBe(new Dictionary<string, long> { ["ordersRejected"] = 1, ["rejections.out_of_stock"] = 1 });
    }

    [Theory]
    [InlineData(0.1, 10)]
    [InlineData(19.995, 2000)]
    [InlineData(1234.56, 123456)]
    public void Money_is_converted_to_minor_units_without_floating_point_drift(double amount, long expected) =>
        BusinessMetricsCalculator.ToMinorUnits((decimal)amount).ShouldBe(expected);

    private static PaymentsV1.PaymentCaptured Captured(decimal amount) => new()
    {
        OrderId = OrderId,
        CustomerId = CustomerId,
        PaymentId = Guid.NewGuid(),
        Amount = new MoneyDto { Amount = amount, Currency = "PLN" },
        ProviderReference = "psp-1",
        CapturedAt = DateTimeOffset.UtcNow,
    };

    private static MessageEnvelope Envelope(PaymentsV1.PaymentCaptured message) => new()
    {
        MessageId = Guid.CreateVersion7(),
        MessageType = "PaymentCaptured",
        ContractVersion = 1,
        CorrelationId = "corr",
        Source = MessagingTopology.Sources.Payments,
        OccurredAt = new DateTimeOffset(2026, 11, 22, 12, 34, 56, TimeSpan.Zero),
        SessionId = message.OrderId.ToString("D"),
        Payload = JsonSerializer.SerializeToUtf8Bytes(message, ContractJson.Options),
    };

    private static BusinessEvent Event(string type, int version, string payload) => new()
    {
        EventId = Guid.CreateVersion7(),
        EventType = type,
        ContractVersion = version,
        Source = "test",
        CorrelationId = "corr",
        OccurredAt = DateTimeOffset.UtcNow,
        Payload = JsonDocument.Parse(payload).RootElement.Clone(),
    };
}

public sealed class FunnelAggregatorTests
{
    private static readonly DateTimeOffset Enqueued = new(2026, 11, 22, 12, 0, 30, TimeSpan.Zero);

    [Fact]
    public void Events_are_summed_per_minute_and_type_before_any_write()
    {
        var increments = FunnelAggregator.Aggregate(
        [
            (Click(ClickstreamEventTypes.ProductView, Enqueued.AddSeconds(-10)), Enqueued),
            (Click(ClickstreamEventTypes.ProductView, Enqueued.AddSeconds(-5)), Enqueued),
            (Click(ClickstreamEventTypes.AddToCart, Enqueued.AddSeconds(-1)), Enqueued),
            (Click(ClickstreamEventTypes.CheckoutStarted, Enqueued.AddSeconds(40)), Enqueued),
        ]);

        increments.Count.ShouldBe(2);
        var first = increments.Single(increment => increment.Window.Id == "funnel:2026-11-22T12:00");
        first.Counters["product_view"].ShouldBe(2);
        first.Counters["add_to_cart"].ShouldBe(1);
        first.Counters["events"].ShouldBe(3);
        increments.Single(increment => increment.Window.Id == "funnel:2026-11-22T12:01").Counters["checkout_started"].ShouldBe(1);
    }

    [Fact]
    public void Browser_clock_far_from_the_enqueue_time_is_not_trusted()
    {
        FunnelAggregator.EventTime(Enqueued.AddDays(-1), Enqueued).ShouldBe(Enqueued);
        FunnelAggregator.EventTime(Enqueued.AddMinutes(-2), Enqueued).ShouldBe(Enqueued.AddMinutes(-2));
    }

    private static ClickstreamEvent Click(string type, DateTimeOffset occurredAt) => new()
    {
        EventId = Guid.NewGuid(),
        SessionId = "sess-1234567890",
        EventType = type,
        OccurredAt = occurredAt,
    };
}

public sealed class MetricStoreTests
{
    [Fact]
    public void Counter_names_are_escaped_into_json_patch_paths()
    {
        CosmosMetricStore.CounterPath("revenueMinor.PLN").ShouldBe("/counters/revenueMinor.PLN");
        CosmosMetricStore.CounterPath("rejections.a/b~c").ShouldBe("/counters/rejections.a~1b~0c");
    }

    [Fact]
    public void Read_range_is_expanded_to_minute_windows_with_an_upper_bound()
    {
        var from = new DateTimeOffset(2026, 11, 22, 12, 0, 59, TimeSpan.Zero);

        CosmosMetricStore.WindowsBetween(MetricNames.Business, from, from.AddMinutes(2)).Select(window => window.Id)
            .ShouldBe(["business:2026-11-22T12:00", "business:2026-11-22T12:01", "business:2026-11-22T12:02"]);
        CosmosMetricStore.WindowsBetween(MetricNames.Business, from, from.AddDays(2)).Count().ShouldBe(CosmosMetricStore.MaxWindowsPerRead);
    }
}
