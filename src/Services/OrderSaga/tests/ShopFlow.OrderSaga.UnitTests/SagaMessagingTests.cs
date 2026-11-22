using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Common;
using ShopFlow.Contracts.Inventory.V1;
using ShopFlow.Contracts.Payments.V1;
using ShopFlow.Messaging;
using ShopFlow.OrderSaga.Messaging;
using ShopFlow.OrderSaga.Model;
using V2 = ShopFlow.Contracts.Orders.V2;

namespace ShopFlow.OrderSaga.UnitTests;

public sealed class SagaReplyMapperTests
{
    [Fact]
    public void Every_reply_type_the_saga_subscribes_to_has_a_mapping()
    {
        // The list matches the "saga" subscription filters; a missing mapping would dead-letter a valid reply.
        SagaReplyMapper.HandledTypes.Count.ShouldBe(13);
        SagaReplyMapper.HandledTypes.ShouldAllBe(type => typeof(IMessage).IsAssignableFrom(type));
    }

    [Fact]
    public void Reply_is_routed_to_the_event_named_after_the_command_it_answers()
    {
        var orderId = Guid.NewGuid();

        var declined = SagaReplyMapper.Map(new PaymentDeclined { OrderId = orderId, CustomerId = Guid.NewGuid(), DeclineCode = "card_expired", DeclinedAt = DateTimeOffset.UtcNow });

        declined.InstanceId.ShouldBe($"order-{orderId:D}");
        declined.EventName.ShouldBe(SagaEvents.AuthorizePayment);
        declined.Reply.Succeeded.ShouldBeFalse();
        declined.Reply.ReasonCode.ShouldBe("card_expired");
    }

    [Fact]
    public void Stock_reservation_carries_the_warehouse_needed_for_the_shipment()
    {
        var reserved = SagaReplyMapper.Map(new StockReserved
        {
            OrderId = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            ReservationId = Guid.NewGuid(),
            WarehouseCode = "KRK-2",
            Lines = [],
            ReservedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30),
        });

        reserved.Reply.WarehouseCode.ShouldBe("KRK-2");
    }
}

public sealed class StartSagaHandlerTests
{
    private readonly DurableTaskClient _client = Substitute.For<DurableTaskClient>("test");
    private readonly StartSagaHandler _handler;

    public StartSagaHandlerTests()
    {
        _handler = new StartSagaHandler(
            new DurableClientHolder { Client = _client },
            Options.Create(new SagaOptions { PaymentTimeout = TimeSpan.FromMinutes(20) }),
            NullLogger<StartSagaHandler>.Instance);
    }

    [Fact]
    public async Task New_order_starts_one_instance_with_a_deterministic_id_and_captured_timeouts()
    {
        var message = OrderSubmitted();
        OrderSagaInput? input = null;
        _client.ScheduleNewOrchestrationInstanceAsync(
                Arg.Any<TaskName>(),
                Arg.Do<object?>(value => input = (OrderSagaInput?)value),
                Arg.Any<StartOrchestrationOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns("order-x");

        await _handler.HandleAsync(message, Context(), TestContext.Current.CancellationToken);

        await _client.Received(1).ScheduleNewOrchestrationInstanceAsync(
            Arg.Is<TaskName>(name => name.Name == OrderSagaInstance.OrchestratorName),
            Arg.Any<object?>(),
            Arg.Is<StartOrchestrationOptions?>(options => options!.InstanceId == $"order-{message.OrderId:D}"),
            Arg.Any<CancellationToken>());

        input!.Timeouts.Payment.ShouldBe(TimeSpan.FromMinutes(20));
        input.CorrelationId.ShouldBe("corr-1");
        input.Delivery.IsPickupPoint.ShouldBeFalse();
    }

    [Fact]
    public async Task Redelivered_order_submitted_does_not_restart_a_finished_saga()
    {
        var message = OrderSubmitted();
        _client.GetInstanceAsync($"order-{message.OrderId:D}", Arg.Any<CancellationToken>())
            .Returns(new OrchestrationMetadata(OrderSagaInstance.OrchestratorName, $"order-{message.OrderId:D}")
            {
                RuntimeStatus = OrchestrationRuntimeStatus.Completed,
            });

        await _handler.HandleAsync(message, Context(), TestContext.Current.CancellationToken);

        await _client.DidNotReceive().ScheduleNewOrchestrationInstanceAsync(
            Arg.Any<TaskName>(), Arg.Any<object?>(), Arg.Any<StartOrchestrationOptions?>(), Arg.Any<CancellationToken>());
    }

    private static V2.OrderSubmitted OrderSubmitted() => new()
    {
        OrderId = Guid.CreateVersion7(),
        CustomerId = Guid.CreateVersion7(),
        OrderNumber = "SF-1",
        Lines = [new OrderLineDto { Sku = "SKU-1", ProductName = "Kubek", Quantity = 1, UnitPrice = new MoneyDto { Amount = 10m, Currency = "PLN" } }],
        Total = new MoneyDto { Amount = 10m, Currency = "PLN" },
        Delivery = new V2.DeliveryDto
        {
            Method = V2.DeliveryMethod.Courier,
            Address = new AddressDto { RecipientName = "Jan", Line1 = "ul. Prosta 1", City = "Warszawa", PostalCode = "00-001", CountryCode = "PL" },
        },
        PaymentMethodToken = "pm",
        SubmittedAt = DateTimeOffset.UtcNow,
    };

    private static MessageContext Context() => new()
    {
        MessageId = Guid.CreateVersion7(),
        MessageType = "OrderSubmitted",
        ContractVersion = 2,
        CorrelationId = "corr-1",
        Source = "orders",
        OccurredAt = DateTimeOffset.UtcNow,
        Consumer = MessagingTopology.Consumers.OrderSaga,
        DeliveryCount = 1,
        EnqueuedAt = DateTimeOffset.UtcNow,
    };
}
