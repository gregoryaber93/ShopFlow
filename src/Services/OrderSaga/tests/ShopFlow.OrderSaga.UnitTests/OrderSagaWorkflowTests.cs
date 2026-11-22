using ShopFlow.Contracts.Orders.V1;
using ShopFlow.OrderSaga.Model;
using ShopFlow.OrderSaga.Workflow;
using static ShopFlow.OrderSaga.UnitTests.SagaTestData;

namespace ShopFlow.OrderSaga.UnitTests;

public sealed class OrderSagaWorkflowTests
{
    [Fact]
    public async Task Happy_path_runs_all_steps_in_order_and_confirms_the_order()
    {
        var context = new FakeOrchestrationContext(Input());

        var outcome = await new OrderSagaWorkflow(context).RunAsync();

        outcome.ShouldBe(SagaOutcome.Confirmed);
        context.SentContracts.ShouldBe(["ReserveStock", "AuthorizePayment", "CreateShipment", "CapturePayment"]);
        context.Activities.ShouldContain(call => call.Name == SagaActivityNames.ConfirmOrder);
        context.Statuses[^1].Status.ShouldBe(SagaStatus.Confirmed);
    }

    [Fact]
    public async Task Out_of_stock_rejects_the_order_without_anything_to_compensate()
    {
        var context = new FakeOrchestrationContext(Input()).Fail(SagaEvents.ReserveStock, "insufficient_stock");

        var outcome = await new OrderSagaWorkflow(context).RunAsync();

        outcome.ShouldBe(SagaOutcome.Rejected);

        // A clear refusal: there is no reservation, so there is no ReleaseStock either.
        context.SentContracts.ShouldBe(["ReserveStock"]);
        RejectionReason(context).ShouldBe(OrderRejectionReasons.OutOfStock);
    }

    [Fact]
    public async Task Stock_timeout_still_releases_stock_because_the_reservation_might_exist()
    {
        var context = new FakeOrchestrationContext(Input()).Timeout(SagaEvents.ReserveStock);

        await new OrderSagaWorkflow(context).RunAsync();

        context.SentContracts.ShouldBe(["ReserveStock", "ReleaseStock"]);
        RejectionReason(context).ShouldBe(OrderRejectionReasons.InternalError);
    }

    [Fact]
    public async Task Declined_payment_releases_stock_and_rejects()
    {
        var context = new FakeOrchestrationContext(Input()).Fail(SagaEvents.AuthorizePayment, "insufficient_funds");

        await new OrderSagaWorkflow(context).RunAsync();

        context.SentContracts.ShouldBe(["ReserveStock", "AuthorizePayment", "ReleaseStock"]);
        RejectionReason(context).ShouldBe(OrderRejectionReasons.PaymentDeclined);
    }

    [Fact]
    public async Task Failed_shipment_compensates_in_reverse_order()
    {
        var context = new FakeOrchestrationContext(Input()).Fail(SagaEvents.CreateShipment, "address_undeliverable");

        await new OrderSagaWorkflow(context).RunAsync();

        context.SentContracts.ShouldBe(["ReserveStock", "AuthorizePayment", "CreateShipment", "VoidPayment", "ReleaseStock"]);
        RejectionReason(context).ShouldBe(OrderRejectionReasons.ShipmentUnavailable);
    }

    [Fact]
    public async Task Capture_timeout_refunds_instead_of_voiding()
    {
        var context = new FakeOrchestrationContext(Input()).Timeout(SagaEvents.CapturePayment);

        await new OrderSagaWorkflow(context).RunAsync();

        context.SentContracts.ShouldBe(["ReserveStock", "AuthorizePayment", "CreateShipment", "CapturePayment", "CancelShipment", "RefundPayment", "ReleaseStock"]);
    }

    [Fact]
    public async Task Explicit_capture_failure_voids_the_authorization()
    {
        var context = new FakeOrchestrationContext(Input()).Fail(SagaEvents.CapturePayment, "authorization_expired");

        await new OrderSagaWorkflow(context).RunAsync();

        context.SentContracts.ShouldBe(["ReserveStock", "AuthorizePayment", "CreateShipment", "CapturePayment", "CancelShipment", "VoidPayment", "ReleaseStock"]);
    }

    [Fact]
    public async Task Cancellation_during_payment_finishes_the_step_then_compensates_and_cancels_the_order()
    {
        var context = new FakeOrchestrationContext(Input()).CancelWhenAwaiting(SagaEvents.AuthorizePayment);

        var outcome = await new OrderSagaWorkflow(context).RunAsync();

        outcome.ShouldBe(SagaOutcome.Cancelled);
        context.SentContracts.ShouldBe(["ReserveStock", "AuthorizePayment", "VoidPayment", "ReleaseStock"]);
        context.Activities.ShouldContain(call => call.Name == SagaActivityNames.CompleteOrderCancellation);
        context.Activities.ShouldNotContain(call => call.Name == SagaActivityNames.RejectOrder);
    }

    [Fact]
    public async Task Cancellation_seen_only_by_orders_at_confirmation_is_honoured()
    {
        var context = new FakeOrchestrationContext(Input());
        context.ActivityResults[SagaActivityNames.ConfirmOrder] = _ => OrdersCallOutcome.CancellationPending;

        var outcome = await new OrderSagaWorkflow(context).RunAsync();

        outcome.ShouldBe(SagaOutcome.Cancelled);
        context.SentContracts.Skip(4).ShouldBe(["CancelShipment", "RefundPayment", "ReleaseStock"]);
    }

    [Fact]
    public void Failed_compensation_stops_the_saga_until_an_operator_decides()
    {
        var context = new FakeOrchestrationContext(Input())
            .Fail(SagaEvents.AuthorizePayment, "insufficient_funds")
            .Timeout(SagaEvents.ReleaseStock);

        var run = new OrderSagaWorkflow(context).RunAsync();

        run.IsCompleted.ShouldBeFalse();
        context.Statuses[^1].Status.ShouldBe(SagaStatus.RequiresManualIntervention);
        context.Activities.ShouldNotContain(call => call.Name == SagaActivityNames.RejectOrder);
    }

    [Fact]
    public async Task Operator_retry_resends_the_compensation_with_a_new_message_id()
    {
        var context = new FakeOrchestrationContext(Input())
            .Fail(SagaEvents.AuthorizePayment, "insufficient_funds")
            .Timeout(SagaEvents.ReleaseStock)
            .Resolve(OperatorAction.Retry);

        var outcome = await new OrderSagaWorkflow(context).RunAsync();

        outcome.ShouldBe(SagaOutcome.Rejected);
        var releases = context.SentCommands.Where(command => command.ContractName == "ReleaseStock").ToList();
        releases.Count.ShouldBe(2);

        // The same MessageId would be dropped by Service Bus duplicate detection.
        releases[0].MessageId.ShouldNotBe(releases[1].MessageId);
    }

    [Fact]
    public async Task Orders_api_outage_escalates_and_operator_can_close_the_case()
    {
        var context = new FakeOrchestrationContext(Input()).Resolve(OperatorAction.MarkResolved);
        context.ActivityResults[SagaActivityNames.ConfirmOrder] = _ => throw new HttpRequestException("orders-api unavailable");

        var outcome = await new OrderSagaWorkflow(context).RunAsync();

        outcome.ShouldBe(SagaOutcome.Confirmed);
        context.Statuses.ShouldContain(status => status.Status == SagaStatus.RequiresManualIntervention);
    }

    [Fact]
    public async Task Replay_produces_identical_commands_with_identical_message_ids()
    {
        // Durable Functions replays the orchestrator many times; every replay must send the same thing.
        var first = new FakeOrchestrationContext(Input());
        var second = new FakeOrchestrationContext(Input());

        await new OrderSagaWorkflow(first).RunAsync();
        await new OrderSagaWorkflow(second).RunAsync();

        second.SentCommands.ShouldBe(first.SentCommands);
    }

    [Fact]
    public async Task Pickup_point_order_uses_create_shipment_v2_and_courier_order_v1()
    {
        var pickup = new FakeOrchestrationContext(Input(pickupPoint: true));
        var courier = new FakeOrchestrationContext(Input());

        await new OrderSagaWorkflow(pickup).RunAsync();
        await new OrderSagaWorkflow(courier).RunAsync();

        pickup.SentCommands.Single(command => command.ContractName == "CreateShipment").ContractVersion.ShouldBe(2);
        courier.SentCommands.Single(command => command.ContractName == "CreateShipment").ContractVersion.ShouldBe(1);
    }

    private static string RejectionReason(FakeOrchestrationContext context) =>
        ((RejectOrderInput)context.Activities.Single(call => call.Name == SagaActivityNames.RejectOrder).Input!).ReasonCode;
}
