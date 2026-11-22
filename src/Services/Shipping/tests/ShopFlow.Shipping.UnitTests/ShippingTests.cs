using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Common;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Handling;
using ShopFlow.Messaging.Inbox;
using ShopFlow.Messaging.Outbox;
using ShopFlow.Shipping.Application;
using ShopFlow.Shipping.Domain;
using ShopFlow.Shipping.Infrastructure;
using V1 = ShopFlow.Contracts.Shipping.V1;
using V2 = ShopFlow.Contracts.Shipping.V2;

namespace ShopFlow.Shipping.UnitTests;

public sealed class ShipmentHandlerTests : IDisposable
{
    private static readonly Guid OrderId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000a1");
    private static readonly Guid CustomerId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000a2");
    private static readonly Guid ShipmentId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000a3");

    private readonly FakeCarrier _carrier = new();
    private readonly ShipmentStore _store = new();
    private readonly ServiceProvider _provider;

    public ShipmentHandlerTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero)));
        services.AddSingleton(_store);
        services.AddSingleton<ICarrierGateway>(_carrier);
        services.AddSingleton<ILabelStorage, FakeLabels>();
        services.AddScoped<IShipmentRepository, InMemoryShipments>();
        services.AddShippingMessaging()
            .AddHandler<V1.CreateShipment, CreateShipmentHandler>()
            .AddHandler<V2.CreateShipment, CreateShipmentHandler>()
            .AddHandler<V1.CancelShipment, CancelShipmentHandler>();
        services.AddShippingApplication();
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public void Dispose() => _provider.Dispose();

    [Fact]
    public async Task Courier_shipment_v1_is_created_with_a_stored_label()
    {
        await DispatchAsync(CreateV1());

        var created = Published<V1.ShipmentCreated>().ShouldHaveSingleItem();
        created.TrackingNumber.ShouldBe("TRK-1");
        created.LabelBlobPath.ShouldBe($"{OrderId:D}/{ShipmentId:D}.pdf");
        _carrier.Requests.Single().Destination.IsPickupPoint.ShouldBeFalse();
    }

    [Fact]
    public async Task Pickup_point_shipment_v2_reaches_the_carrier_as_a_pickup_point()
    {
        await DispatchAsync(new V2.CreateShipment
        {
            OrderId = OrderId,
            CustomerId = CustomerId,
            OrderNumber = "SF-1",
            ShipmentId = ShipmentId,
            WarehouseCode = "WAW-1",
            Destination = new V2.ShipmentDestinationDto { Method = V2.ShipmentDeliveryMethod.PickupPoint, PickupPointId = "WAW-PP-7" },
            Lines = [new StockLineDto { Sku = "SKU-1", Quantity = 1 }],
        });

        _carrier.Requests.Single().Destination.PickupPointId.ShouldBe("WAW-PP-7");
        Published<V1.ShipmentCreated>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Carrier_refusal_is_answered_with_creation_failed()
    {
        _carrier.Next = ParcelResult.Refused("address_undeliverable", "Unknown street");

        await DispatchAsync(CreateV1());

        Published<V1.ShipmentCreationFailed>().ShouldHaveSingleItem().ReasonCode.ShouldBe("address_undeliverable");
        _store.Shipments.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancel_that_overtakes_create_blocks_the_late_creation_without_calling_the_carrier()
    {
        await DispatchAsync(new V1.CancelShipment { OrderId = OrderId, CustomerId = CustomerId, ShipmentId = ShipmentId, Reason = "payment_declined" });
        await DispatchAsync(CreateV1());

        _carrier.Requests.ShouldBeEmpty();
        Published<V1.ShipmentCancelled>().ShouldHaveSingleItem();
        Published<V1.ShipmentCreationFailed>().ShouldHaveSingleItem().ReasonCode.ShouldBe("shipment_cancelled");
    }

    [Fact]
    public async Task Carrier_events_move_the_shipment_and_are_deduplicated()
    {
        await DispatchAsync(CreateV1());

        await WebhookAsync("evt-1", "parcel.delivered");
        await Should.ThrowAsync<DuplicateMessageException>(() => WebhookAsync("evt-1", "parcel.delivered"));

        Published<V1.ShipmentDelivered>().ShouldHaveSingleItem();
        _store.Shipments.Values.Single().Status.ShouldBe(ShipmentStatus.Delivered);
    }

    private static V1.CreateShipment CreateV1() => new()
    {
        OrderId = OrderId,
        CustomerId = CustomerId,
        OrderNumber = "SF-1",
        ShipmentId = ShipmentId,
        WarehouseCode = "WAW-1",
        Destination = new AddressDto { RecipientName = "Jan", Line1 = "ul. Prosta 1", City = "Warszawa", PostalCode = "00-001", CountryCode = "PL" },
        Lines = [new StockLineDto { Sku = "SKU-1", Quantity = 1 }],
    };

    private Task<DispatchOutcome> DispatchAsync(IMessage message) =>
        _provider.GetRequiredService<MessageDispatcher>().DispatchAsync(
            _provider.GetRequiredService<MessageEnvelopeFactory>().Create(message, new MessageMetadata("corr")),
            new ConsumerEndpoint(MessagingTopology.Consumers.Shipping),
            new DeliveryInfo(1, DateTimeOffset.UtcNow),
            CancellationToken.None);

    private async Task WebhookAsync(string eventId, string type)
    {
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MessageContextAccessor>().SetIncoming(new MessageContext
        {
            MessageId = DeterministicGuid.Create($"carrier-webhook/{eventId}"),
            MessageType = type,
            ContractVersion = 1,
            CorrelationId = "c",
            Source = "carrier",
            OccurredAt = DateTimeOffset.UtcNow,
            Consumer = "carrier-webhook",
            DeliveryCount = 1,
            EnqueuedAt = DateTimeOffset.UtcNow,
        });

        await scope.ServiceProvider.GetRequiredService<CarrierWebhookProcessor>()
            .ProcessAsync(new CarrierEvent(eventId, type, OrderId, ShipmentId, DateTimeOffset.UtcNow, null), CancellationToken.None);
    }

    private IReadOnlyList<T> Published<T>()
    {
        var name = MessageTypeRegistry.Default.GetContractId(typeof(T)).Name;
        return [.. _store.Published.Where(message => message.Envelope.MessageType == name).Select(message => JsonSerializer.Deserialize<T>(message.Envelope.Payload.Span, ContractJson.Options)!)];
    }

    private sealed class ShipmentStore
    {
        public Dictionary<Guid, Shipment> Shipments { get; } = [];

        public List<OutboundMessage> Published { get; } = [];

        public HashSet<InboxEntry> Inbox { get; } = [];
    }

    private sealed class InMemoryShipments(ShipmentStore store, IPendingOutbox outbox, IMessageContextAccessor context) : IShipmentRepository
    {
        public Task<Shipment?> GetAsync(Guid orderId, Guid shipmentId, CancellationToken cancellationToken) =>
            Task.FromResult(store.Shipments.GetValueOrDefault(shipmentId));

        public Task SaveAsync(Shipment shipment, CancellationToken cancellationToken)
        {
            store.Shipments[shipment.Id] = shipment;
            return CommitWithoutShipmentAsync(shipment.OrderId, cancellationToken);
        }

        public Task CommitWithoutShipmentAsync(Guid orderId, CancellationToken cancellationToken)
        {
            if (context.Current is { } incoming && !store.Inbox.Add(incoming.InboxEntry))
            {
                throw new DuplicateMessageException(incoming.InboxEntry);
            }

            store.Published.AddRange(outbox.Drain());
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCarrier : ICarrierGateway
    {
        public ParcelResult Next { get; set; } = ParcelResult.Created("TRK-1", "%PDF-label"u8.ToArray());

        public List<ParcelRequest> Requests { get; } = [];

        public string CarrierCode => "DPD";

        public Task<ParcelResult> CreateParcelAsync(ParcelRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Next);
        }

        public Task<bool> CancelParcelAsync(Guid shipmentId, string trackingNumber, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FakeLabels : ILabelStorage
    {
        public Task<string> UploadAsync(Guid orderId, Guid shipmentId, byte[] labelPdf, CancellationToken cancellationToken) =>
            Task.FromResult($"{orderId:D}/{shipmentId:D}.pdf");
    }
}

public sealed class ShipmentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Delivery_without_a_dispatch_event_still_completes_and_a_late_dispatch_changes_nothing()
    {
        var shipment = Created();

        shipment.MarkDelivered(Now).IsSuccess.ShouldBeTrue();
        shipment.MarkDispatched(Now, null).IsSuccess.ShouldBeTrue();

        shipment.Status.ShouldBe(ShipmentStatus.Delivered);
        shipment.DomainEvents.OfType<ShipmentDeliveredDomainEvent>().ShouldHaveSingleItem();
        shipment.DomainEvents.OfType<ShipmentDispatchedDomainEvent>().ShouldBeEmpty();
    }

    [Fact]
    public void Dispatched_parcel_cannot_be_cancelled()
    {
        var shipment = Created();
        shipment.MarkDispatched(Now, null);

        shipment.Cancel(Now).Error.Code.ShouldBe("shipment.already_dispatched");
    }

    private static Shipment Created() => Shipment.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "SF-1", "WAW-1",
        new ShipmentDestination(false, null, "Jan", "ul. Prosta 1", null, "Warszawa", "00-001", "PL", null),
        "DPD", "TRK-1", "label.pdf", Now);
}
