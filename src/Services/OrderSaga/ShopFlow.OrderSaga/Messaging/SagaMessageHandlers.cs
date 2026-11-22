using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Inventory.V1;
using ShopFlow.Contracts.Orders.V1;
using ShopFlow.Contracts.Payments.V1;
using ShopFlow.Contracts.Shipping.V1;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Handling;
using ShopFlow.OrderSaga.Model;
using V2 = ShopFlow.Contracts.Orders.V2;

namespace ShopFlow.OrderSaga.Messaging;

/// <summary>
/// Durable Functions client passed from the function's <c>[DurableClient]</c> binding into the handler's DI scope.
/// </summary>
public sealed class DurableClientHolder
{
    private DurableTaskClient? _client;

    public DurableTaskClient Client
    {
        get => _client ?? throw new InvalidOperationException("DurableTaskClient was not provided for this scope.");
        set => _client = value;
    }
}

/// <summary>Starts the saga after OrderSubmitted v2. Exactly one instance per order.</summary>
public sealed partial class StartSagaHandler(
    DurableClientHolder durable,
    IOptions<SagaOptions> options,
    ILogger<StartSagaHandler> logger) : IMessageHandler<V2.OrderSubmitted>
{
    public async Task HandleAsync(V2.OrderSubmitted message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(context);

        var client = durable.Client;
        var instanceId = OrderSagaInstance.IdFor(message.OrderId);

        // WHY: check first whether the instance exists. Starting with the identifier of an instance that has already
        // completed is not rejected; it restarts the instance from scratch. A redelivered OrderSubmitted (e.g. replayed
        // from the DLQ) would start a second saga for the same order and charge the card again.
        if (await client.GetInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false) is not null)
        {
            LogAlreadyStarted(instanceId, context.MessageId);
            return;
        }

        var input = new OrderSagaInput
        {
            OrderId = message.OrderId,
            CustomerId = message.CustomerId,
            OrderNumber = message.OrderNumber,
            Lines = [.. message.Lines.Select(line => new ShopFlow.Contracts.Common.StockLineDto { Sku = line.Sku, Quantity = line.Quantity })],
            Total = message.Total,
            Delivery = new SagaDelivery(
                message.Delivery.Method == V2.DeliveryMethod.PickupPoint,
                message.Delivery.Address,
                message.Delivery.PickupPointId),
            PaymentMethodToken = message.PaymentMethodToken,
            CorrelationId = context.CorrelationId,
            TriggeringMessageId = context.MessageId.ToString("D"),
            Timeouts = options.Value.ToTimeouts(),
        };

        try
        {
            await client.ScheduleNewOrchestrationInstanceAsync(
                OrderSagaInstance.OrchestratorName,
                input,
                new StartOrchestrationOptions(instanceId),
                cancellationToken).ConfigureAwait(false);

            LogStarted(instanceId, message.OrderNumber);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Two copies of the same message processed in parallel (e.g. after a lost lock): the second hit an instance that
            // was being created. The scheduler rejects a duplicate of an active instance and we swallow that.
            // Any other error (the instance was not created) propagates and the message will be retried.
            if (await client.GetInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false) is null)
            {
                throw;
            }

            LogAlreadyStarted(instanceId, context.MessageId);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Order saga {InstanceId} started for order {OrderNumber}")]
    private partial void LogStarted(string instanceId, string orderNumber);

    [LoggerMessage(Level = LogLevel.Information, Message = "Order saga {InstanceId} already exists; duplicate OrderSubmitted {MessageId} ignored")]
    private partial void LogAlreadyStarted(string instanceId, Guid messageId);
}

/// <summary>
/// Turns participant replies (from the saga-replies queue) into external events of the instance.
/// </summary>
/// <remarks>
/// WHY: no inbox. Raising the same event twice is harmless: the orchestrator receives each step's event exactly once,
/// and the duplicate stays in the instance's buffer and disappears together with it.
/// <para>
/// AZURE: the saga-replies queue has sessions enabled (SessionId = order identifier). Replies for one order are passed
/// to the instance in order and by one process at a time, while different orders are handled in parallel.
/// </para>
/// </remarks>
public sealed partial class SagaReplyHandler(DurableClientHolder durable, ILogger<SagaReplyHandler> logger) :
    IMessageHandler<StockReserved>,
    IMessageHandler<StockReservationFailed>,
    IMessageHandler<StockReleased>,
    IMessageHandler<PaymentAuthorized>,
    IMessageHandler<PaymentDeclined>,
    IMessageHandler<PaymentCaptured>,
    IMessageHandler<PaymentCaptureFailed>,
    IMessageHandler<PaymentVoided>,
    IMessageHandler<PaymentRefunded>,
    IMessageHandler<ShipmentCreated>,
    IMessageHandler<ShipmentCreationFailed>,
    IMessageHandler<ShipmentCancelled>,
    IMessageHandler<OrderCancellationRequested>
{
    public Task HandleAsync(StockReserved message, MessageContext context, CancellationToken cancellationToken) => RaiseAsync(message, cancellationToken);

    public Task HandleAsync(StockReservationFailed message, MessageContext context, CancellationToken cancellationToken) => RaiseAsync(message, cancellationToken);

    public Task HandleAsync(StockReleased message, MessageContext context, CancellationToken cancellationToken) => RaiseAsync(message, cancellationToken);

    public Task HandleAsync(PaymentAuthorized message, MessageContext context, CancellationToken cancellationToken) => RaiseAsync(message, cancellationToken);

    public Task HandleAsync(PaymentDeclined message, MessageContext context, CancellationToken cancellationToken) => RaiseAsync(message, cancellationToken);

    public Task HandleAsync(PaymentCaptured message, MessageContext context, CancellationToken cancellationToken) => RaiseAsync(message, cancellationToken);

    public Task HandleAsync(PaymentCaptureFailed message, MessageContext context, CancellationToken cancellationToken) => RaiseAsync(message, cancellationToken);

    public Task HandleAsync(PaymentVoided message, MessageContext context, CancellationToken cancellationToken) => RaiseAsync(message, cancellationToken);

    public Task HandleAsync(PaymentRefunded message, MessageContext context, CancellationToken cancellationToken) => RaiseAsync(message, cancellationToken);

    public Task HandleAsync(ShipmentCreated message, MessageContext context, CancellationToken cancellationToken) => RaiseAsync(message, cancellationToken);

    public Task HandleAsync(ShipmentCreationFailed message, MessageContext context, CancellationToken cancellationToken) => RaiseAsync(message, cancellationToken);

    public Task HandleAsync(ShipmentCancelled message, MessageContext context, CancellationToken cancellationToken) => RaiseAsync(message, cancellationToken);

    public Task HandleAsync(OrderCancellationRequested message, MessageContext context, CancellationToken cancellationToken) => RaiseAsync(message, cancellationToken);

    private async Task RaiseAsync(IMessage message, CancellationToken cancellationToken)
    {
        var signal = SagaReplyMapper.Map(message);
        var client = durable.Client;

        var instance = await client.GetInstanceAsync(signal.InstanceId, cancellationToken).ConfigureAwait(false);
        if (instance is null || instance.IsCompleted)
        {
            // A late reply (e.g. after a timeout and compensation) or a duplicate after the saga finished.
            // A completed instance accepts no more events; we complete the message and there is nothing else to do.
            LogIgnored(signal.EventName, signal.InstanceId, instance?.RuntimeStatus.ToString() ?? "not found");
            return;
        }

        await client.RaiseEventAsync(signal.InstanceId, signal.EventName, signal.Reply, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Saga reply {EventName} for {InstanceId} ignored; instance is {Status}")]
    private partial void LogIgnored(string eventName, string instanceId, string status);
}
