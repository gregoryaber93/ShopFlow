using ShopFlow.Orders.Domain.Orders;
using static ShopFlow.Orders.UnitTests.TestData;

namespace ShopFlow.Orders.UnitTests.Domain;

public sealed class OrderTests
{
    [Fact]
    public void Submitted_order_totals_its_lines_and_raises_one_event()
    {
        var result = Order.Submit(Guid.NewGuid(), CustomerId, "SF-1", [Line("A", 2, 10m), Line("B", 1, 5.5m)], Delivery.Courier(Address), "pm", Now);

        var order = result.Value;
        order.Total.Amount.ShouldBe(25.5m);
        order.Status.ShouldBe(OrderStatus.Submitted);
        order.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<OrderSubmittedDomainEvent>();
        order.History.ShouldHaveSingleItem().Status.ShouldBe(OrderStatus.Submitted);
    }

    [Fact]
    public void Order_without_lines_is_rejected()
    {
        Order.Submit(Guid.NewGuid(), CustomerId, "SF-1", [], Delivery.Courier(Address), "pm", Now)
            .Error.ShouldBe(OrderErrors.NoLines);
    }

    [Fact]
    public void Same_sku_twice_is_rejected()
    {
        Order.Submit(Guid.NewGuid(), CustomerId, "SF-1", [Line("A"), Line("a")], Delivery.Courier(Address), "pm", Now)
            .Error.ShouldBe(OrderErrors.DuplicateSku);
    }

    [Fact]
    public void Lines_in_different_currencies_are_rejected()
    {
        Order.Submit(Guid.NewGuid(), CustomerId, "SF-1", [Line("A"), Line("B", currency: "EUR")], Delivery.Courier(Address), "pm", Now)
            .Error.ShouldBe(OrderErrors.MixedCurrencies);
    }

    [Fact]
    public void Confirm_moves_submitted_order_to_confirmed()
    {
        var order = SubmittedOrder();

        order.Confirm("TRK-9", Now).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(OrderStatus.Confirmed);
        order.TrackingNumber.ShouldBe("TRK-9");
        order.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<OrderConfirmedDomainEvent>();
    }

    [Fact]
    public void Confirming_twice_is_a_silent_no_op()
    {
        // The saga retries the call after a lost response: the second attempt must not raise a second event.
        var order = ConfirmedOrder();

        order.Confirm("TRK-1", Now).IsSuccess.ShouldBeTrue();

        order.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Order_with_pending_cancellation_cannot_be_confirmed()
    {
        var order = SubmittedOrder();
        order.RequestCancellation("customer", "changed my mind", Now);

        order.Confirm(null, Now).Error.ShouldBe(OrderErrors.CancellationPending);
    }

    [Fact]
    public void Cancellation_can_be_requested_only_while_the_saga_runs()
    {
        ConfirmedOrder().RequestCancellation("customer", null, Now).Error.ShouldBe(OrderErrors.CannotCancel);
    }

    [Fact]
    public void Completed_cancellation_closes_the_order()
    {
        var order = SubmittedOrder();
        order.RequestCancellation("customer", "too slow", Now);
        order.DequeueDomainEvents();

        order.CompleteCancellation(Now).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(OrderStatus.Cancelled);
        order.ClosedAt.ShouldBe(Now);
        order.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<OrderCancelledDomainEvent>().Reason.ShouldBe("too slow");
    }

    [Fact]
    public void Saga_can_reject_an_order_even_after_cancellation_was_requested()
    {
        // The saga could have failed (e.g. out of stock) before it handled the cancellation request.
        var order = SubmittedOrder();
        order.RequestCancellation("customer", null, Now);

        order.Reject("out_of_stock", "SKU-1 unavailable", Now).IsSuccess.ShouldBeTrue();

        order.Status.ShouldBe(OrderStatus.Rejected);
    }

    [Fact]
    public void Shipment_event_before_confirmation_is_reported_as_not_ready()
    {
        SubmittedOrder().MarkShipped("TRK-1", Now).Error.ShouldBe(OrderErrors.NotConfirmedYet);
    }

    [Fact]
    public void Delivery_can_complete_a_confirmed_order_when_dispatch_event_is_late()
    {
        var order = ConfirmedOrder();

        order.MarkDelivered(Now).IsSuccess.ShouldBeTrue();
        order.Status.ShouldBe(OrderStatus.Completed);
        order.DequeueDomainEvents().ShouldHaveSingleItem().ShouldBeOfType<OrderCompletedDomainEvent>();

        // A late "dispatched" after delivery: success without a change and without an event.
        order.MarkShipped("TRK-1", Now).IsSuccess.ShouldBeTrue();
        order.Status.ShouldBe(OrderStatus.Completed);
        order.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public void Delivery_of_a_rejected_order_is_a_closed_order_error()
    {
        var order = SubmittedOrder();
        order.Reject("payment_declined", null, Now);

        order.MarkDelivered(Now).Error.ShouldBe(OrderErrors.AlreadyClosed);
    }

    [Fact]
    public void State_round_trip_preserves_everything_including_concurrency_token()
    {
        var order = ConfirmedOrder();

        var restored = Order.Restore(order.ToState());

        restored.ToState().ShouldBeEquivalentTo(order.ToState());
        restored.ConcurrencyToken.ShouldBe("\"etag-1\"");
        restored.DomainEvents.ShouldBeEmpty();
    }
}
