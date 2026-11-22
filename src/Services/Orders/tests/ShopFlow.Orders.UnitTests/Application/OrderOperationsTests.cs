using ShopFlow.Orders.Application.Orders;
using ShopFlow.Orders.Domain.Orders;
using ShopFlow.SharedKernel.Persistence;
using ShopFlow.SharedKernel.Results;
using static ShopFlow.Orders.UnitTests.TestData;

namespace ShopFlow.Orders.UnitTests.Application;

public sealed class OrderOperationsTests : IDisposable
{
    private readonly OrdersTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Saga_confirms_with_current_etag_and_order_confirmed_is_published()
    {
        var (orderId, etag) = await GivenSubmittedOrderAsync();

        var result = await Saga(operations => operations.ConfirmAsync(orderId, etag, "TRK-7", Ct));

        result.Value.Order.Status.ShouldBe(OrderStatus.Confirmed);
        result.Value.ETag.ShouldNotBe(etag);
        _host.Store.PublishedTypes().ShouldBe(["OrderConfirmed.v1"]);
    }

    [Fact]
    public async Task Stale_etag_is_a_concurrency_conflict_and_nothing_changes()
    {
        var (orderId, _) = await GivenSubmittedOrderAsync();

        await Should.ThrowAsync<ConcurrencyConflictException>(() => Saga(operations => operations.ConfirmAsync(orderId, "\"stale\"", null, Ct)));

        _host.Store.Orders[orderId].State.Status.ShouldBe(OrderStatus.Submitted);
        _host.Store.Published.ShouldBeEmpty();
    }

    [Fact]
    public async Task Repeated_confirmation_with_the_new_etag_is_idempotent_and_writes_nothing()
    {
        var (orderId, etag) = await GivenSubmittedOrderAsync();
        var first = await Saga(operations => operations.ConfirmAsync(orderId, etag, null, Ct));

        var second = await Saga(operations => operations.ConfirmAsync(orderId, first.Value.ETag, null, Ct));

        second.Value.ETag.ShouldBe(first.Value.ETag);
        _host.Store.PublishedTypes().ShouldBe(["OrderConfirmed.v1"]);
    }

    [Fact]
    public async Task Customer_cannot_see_or_cancel_someone_elses_order()
    {
        var (orderId, etag) = await GivenSubmittedOrderAsync();

        var result = await _host.InScopeAsync<RequestCancellationHandler, Result<OrderMutation>>(
            handler => handler.HandleAsync(orderId, Guid.NewGuid(), etag, null, Ct));

        result.Error.ShouldBe(OrderErrors.NotFound);
    }

    [Fact]
    public async Task Customer_cancellation_request_is_published_for_the_saga()
    {
        var (orderId, etag) = await GivenSubmittedOrderAsync();

        var result = await _host.InScopeAsync<RequestCancellationHandler, Result<OrderMutation>>(
            handler => handler.HandleAsync(orderId, CustomerId, etag, "found it cheaper", Ct));

        result.Value.Order.Status.ShouldBe(OrderStatus.CancellationRequested);
        _host.Store.PublishedTypes().ShouldBe(["OrderCancellationRequested.v1"]);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Result<OrderMutation>> Saga(Func<SagaOrderOperations, Task<Result<OrderMutation>>> action) =>
        _host.InScopeAsync(action);

    private async Task<(Guid OrderId, string ETag)> GivenSubmittedOrderAsync()
    {
        _host.Catalog.WithPrice("SKU-1", 10m);
        await _host.GivenCartAsync(("SKU-1", 1));

        var submitted = await _host.InScopeAsync<SubmitOrderHandler, Result<SubmittedOrder>>(handler =>
            handler.HandleAsync(new SubmitOrderCommand(CustomerId, new DeliveryInput(DeliveryMethod.Courier, Address, null), "pm"), Ct));

        while (_host.Store.Published.TryDequeue(out _))
        {
        }

        return (submitted.Value.OrderId, submitted.Value.ETag);
    }
}
