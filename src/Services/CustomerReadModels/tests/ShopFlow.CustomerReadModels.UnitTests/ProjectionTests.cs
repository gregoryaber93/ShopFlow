using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Common;
using ShopFlow.CustomerReadModels.Application;
using ShopFlow.CustomerReadModels.Infrastructure;
using ShopFlow.CustomerReadModels.Testing;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Handling;
using OrdersV1 = ShopFlow.Contracts.Orders.V1;
using OrdersV2 = ShopFlow.Contracts.Orders.V2;
using PaymentsV1 = ShopFlow.Contracts.Payments.V1;
using ShippingV1 = ShopFlow.Contracts.Shipping.V1;

namespace ShopFlow.CustomerReadModels.UnitTests;

public sealed class ProjectionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid CustomerId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000e1");
    private static readonly Guid ShipmentId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000e2");

    private readonly Guid _orderId = Guid.CreateVersion7(Now.AddHours(-1));
    private readonly FakeTimeProvider _time = new(Now);
    private readonly InMemoryCustomerOrderStore _store = new();
    private readonly InMemoryOrderDetailsCache _cache = new();
    private readonly ServiceProvider _provider;

    public ProjectionTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(_time);
        services.AddSingleton<ICustomerOrderStore>(_store);
        services.AddSingleton<IOrderDetailsCache>(_cache);
        services.AddCustomerReadModelsMessaging().AddCustomerOrderProjection();
        services.AddCustomerReadModelsApplication();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task Full_order_lifecycle_ends_as_delivered_with_payment_invoice_and_tracking()
    {
        await DispatchAsync(Submitted());
        await DispatchAsync(new PaymentsV1.PaymentAuthorized { OrderId = _orderId, CustomerId = CustomerId, PaymentId = Guid.NewGuid(), Amount = Pln(149.98m), ProviderReference = "psp-1", AuthorizedAt = Now, AuthorizationExpiresAt = Now.AddDays(7) });
        await DispatchAsync(new ShippingV1.ShipmentCreated { OrderId = _orderId, CustomerId = CustomerId, ShipmentId = ShipmentId, CarrierCode = "DPD", TrackingNumber = "TRK-1", LabelBlobPath = "x.pdf", CreatedAt = Now });
        await DispatchAsync(new PaymentsV1.PaymentCaptured { OrderId = _orderId, CustomerId = CustomerId, PaymentId = Guid.NewGuid(), Amount = Pln(149.98m), ProviderReference = "psp-1", CapturedAt = Now });
        await DispatchAsync(Confirmed());
        await DispatchAsync(new PaymentsV1.InvoiceIssued { OrderId = _orderId, CustomerId = CustomerId, InvoiceNumber = "FV-2026-11-000001", Total = Pln(149.98m), BlobPath = "2026/11/fv.pdf", IssuedAt = Now });
        await DispatchAsync(Dispatched());
        await DispatchAsync(new ShippingV1.ShipmentDelivered { OrderId = _orderId, CustomerId = CustomerId, ShipmentId = ShipmentId, DeliveredAt = Now.AddDays(2) });

        var view = await StoredAsync();
        view.Status.ShouldBe(CustomerOrderStatus.Delivered);
        view.OrderNumber.ShouldBe("SF-2026-000001");
        view.Lines.ShouldHaveSingleItem().Quantity.ShouldBe(2);
        view.Payment.CapturedAt.ShouldNotBeNull();
        view.Invoice!.Number.ShouldBe("FV-2026-11-000001");
        view.Shipment!.TrackingNumber.ShouldBe("TRK-1");
        view.AppliedMessageIds.Count.ShouldBe(8);
    }

    [Fact]
    public async Task Late_confirmation_does_not_move_a_shipped_order_back()
    {
        // Ordering across three topics: shipment, then confirmation, and finally the order header.
        await DispatchAsync(Dispatched());
        await DispatchAsync(Confirmed());
        await DispatchAsync(Submitted());

        var view = await StoredAsync();
        view.Status.ShouldBe(CustomerOrderStatus.Shipped);
        view.HasHeader.ShouldBeTrue();
        view.ConfirmedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Event_before_order_submitted_creates_a_hidden_placeholder()
    {
        await DispatchAsync(new PaymentsV1.PaymentAuthorized { OrderId = _orderId, CustomerId = CustomerId, PaymentId = Guid.NewGuid(), Amount = Pln(10m), ProviderReference = "psp-1", AuthorizedAt = Now, AuthorizationExpiresAt = Now.AddDays(7) });

        (await StoredAsync()).HasHeader.ShouldBeFalse();

        var queries = _provider.CreateScope().ServiceProvider.GetRequiredService<CustomerOrderQueries>();
        (await queries.GetAsync(CustomerId, _orderId, CancellationToken.None)).ShouldBe(OrderLookup.NotFound);
        (await queries.ListAsync(CustomerId, 20, null, CancellationToken.None)).Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task Redelivered_refund_is_counted_once_and_separate_refunds_add_up()
    {
        await DispatchAsync(Submitted());
        var firstRefund = Envelope(Refund(50m));

        await DispatchAsync(firstRefund);
        await DispatchAsync(firstRefund);
        await DispatchAsync(Refund(30m));
        await DispatchAsync(Refund(0m));

        (await StoredAsync()).Payment.RefundedAmount.ShouldBe(new MoneyView(80m, "PLN"));
    }

    [Fact]
    public async Task Version_1_of_order_submitted_is_ignored_because_version_2_is_published_alongside()
    {
        var outcome = await DispatchAsync(new OrdersV1.OrderSubmitted
        {
            OrderId = _orderId,
            CustomerId = CustomerId,
            OrderNumber = "SF-2026-000001",
            Lines = [],
            Total = Pln(1m),
            ShippingAddress = new AddressDto { RecipientName = "Jan", Line1 = "ul. Prosta 1", City = "Warszawa", PostalCode = "00-001", CountryCode = "PL" },
            PaymentMethodToken = "tok",
            SubmittedAt = Now,
        });

        outcome.ShouldBe(DispatchOutcome.Ignored);
        _store.All.ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_projection_invalidates_the_cached_details()
    {
        await DispatchAsync(Submitted());
        await DispatchAsync(Confirmed());

        _cache.Invalidations.ShouldBe([_orderId, _orderId]);
    }

    [Fact]
    public async Task Cached_order_of_another_customer_is_not_returned()
    {
        await DispatchAsync(Submitted());
        var queries = _provider.CreateScope().ServiceProvider.GetRequiredService<CustomerOrderQueries>();
        (await queries.GetAsync(CustomerId, _orderId, CancellationToken.None)).View.ShouldNotBeNull();
        _cache.Entries.ShouldContainKey(_orderId);

        var stranger = await queries.GetAsync(Guid.NewGuid(), _orderId, CancellationToken.None);

        stranger.ShouldBe(OrderLookup.NotFound);
    }

    [Fact]
    public async Task Order_placed_seconds_ago_is_reported_as_pending_instead_of_missing()
    {
        var freshOrderId = Guid.CreateVersion7(Now.AddSeconds(-5));
        var queries = _provider.CreateScope().ServiceProvider.GetRequiredService<CustomerOrderQueries>();

        (await queries.GetAsync(CustomerId, freshOrderId, CancellationToken.None)).ShouldBe(OrderLookup.Pending);
        (await queries.GetAsync(CustomerId, _orderId, CancellationToken.None)).ShouldBe(OrderLookup.NotFound);
    }

    private OrdersV2.OrderSubmitted Submitted() => new()
    {
        OrderId = _orderId,
        CustomerId = CustomerId,
        OrderNumber = "SF-2026-000001",
        Lines = [new OrderLineDto { Sku = "SKU-1", ProductName = "Kubek", Quantity = 2, UnitPrice = Pln(74.99m) }],
        Total = Pln(149.98m),
        Delivery = new OrdersV2.DeliveryDto { Method = OrdersV2.DeliveryMethod.PickupPoint, PickupPointId = "WAW-PP-7" },
        PaymentMethodToken = "tok",
        SubmittedAt = Now.AddMinutes(-1),
    };

    private OrdersV1.OrderConfirmed Confirmed() => new()
    {
        OrderId = _orderId,
        CustomerId = CustomerId,
        OrderNumber = "SF-2026-000001",
        Total = Pln(149.98m),
        ConfirmedAt = Now,
    };

    private ShippingV1.ShipmentDispatched Dispatched() => new()
    {
        OrderId = _orderId,
        CustomerId = CustomerId,
        ShipmentId = ShipmentId,
        CarrierCode = "DPD",
        TrackingNumber = "TRK-1",
        DispatchedAt = Now.AddHours(3),
        EstimatedDeliveryDate = new DateOnly(2026, 11, 24),
    };

    private PaymentsV1.PaymentRefunded Refund(decimal amount) => new()
    {
        OrderId = _orderId,
        CustomerId = CustomerId,
        PaymentId = Guid.NewGuid(),
        Amount = Pln(amount),
        RefundedAt = Now,
    };

    private static MoneyDto Pln(decimal amount) => new() { Amount = amount, Currency = "PLN" };

    private async Task<CustomerOrderView> StoredAsync() =>
        (await _store.GetAsync(CustomerId, _orderId, CancellationToken.None)).ShouldNotBeNull();

    private MessageEnvelope Envelope(IMessage message) =>
        _provider.GetRequiredService<MessageEnvelopeFactory>().Create(message, new MessageMetadata("corr"));

    private Task<DispatchOutcome> DispatchAsync(IMessage message) => DispatchAsync(Envelope(message));

    private Task<DispatchOutcome> DispatchAsync(MessageEnvelope envelope) =>
        _provider.GetRequiredService<MessageDispatcher>().DispatchAsync(
            envelope,
            new ConsumerEndpoint(MessagingTopology.Consumers.ReadModels, UnhandledMessagePolicy.Ignore),
            new DeliveryInfo(1, DateTimeOffset.UtcNow),
            CancellationToken.None);
}

public sealed class UuidV7Tests
{
    [Fact]
    public void Creation_time_is_read_from_a_version_7_identifier()
    {
        var created = new DateTimeOffset(2026, 11, 22, 10, 15, 30, 123, TimeSpan.Zero);

        UuidV7.TryGetTimestamp(Guid.CreateVersion7(created), out var timestamp).ShouldBeTrue();

        timestamp.ShouldBe(created);
    }

    [Fact]
    public void Random_identifier_has_no_timestamp() =>
        UuidV7.TryGetTimestamp(Guid.NewGuid(), out _).ShouldBeFalse();
}

public sealed class ContinuationTokenTests
{
    [Fact]
    public void Cosmos_token_survives_a_round_trip_through_a_url_safe_form()
    {
        const string token = """[{"compositeToken":{"token":"+RID:~abc==#RT:1#TRC:20","range":{"min":"","max":"FF"}}}]""";

        var encoded = CosmosCustomerOrderStore.EncodeToken(token)!;

        encoded.ShouldNotContain("+");
        encoded.ShouldNotContain("/");
        encoded.ShouldNotContain("\"");
        CosmosCustomerOrderStore.DecodeToken(encoded).ShouldBe(token);
    }

    [Fact]
    public void Tampered_token_is_rejected_as_a_bad_argument() =>
        Should.Throw<ArgumentException>(() => CosmosCustomerOrderStore.DecodeToken("not*base64"));
}
