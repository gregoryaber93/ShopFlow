using Microsoft.DurableTask;
using Microsoft.Extensions.Logging;
using ShopFlow.Contracts.Orders.V1;
using ShopFlow.OrderSaga.Model;

namespace ShopFlow.OrderSaga.Workflow;

/// <summary>
/// Order saga flow: reservation → payment authorization → shipment → capture → confirmation.
/// </summary>
/// <remarks>
/// AZURE: this is Durable Functions orchestrator code. It does not behave like a normal method: after every
/// <c>await</c> on a durable task (activity, timer, event) the process may be stopped and the state restored later,
/// even on another instance, by running this method again from the start against the event history (replay).
/// Activity and event results then come from the history, not from a new call. This gives hard rules for the code in
/// this class:
/// <list type="bullet">
/// <item>Determinism: <c>context.CurrentUtcDateTime</c> instead of <c>DateTime.UtcNow</c>, <c>context.NewGuid()</c>
/// instead of <c>Guid.NewGuid()</c>. No reading of configuration or environment variables.</item>
/// <item>No I/O: no HTTP, database or Service Bus calls. Activities do all of that.</item>
/// <item>Logging through a "replay-safe" logger; otherwise every entry would repeat on every replay.</item>
/// <item>Changing the order of durable calls in a new code version breaks the replay of instances that are already
/// running. See orchestration versioning in host.json (defaultVersion, versionMatchStrategy) and
/// <c>context.Version</c>: a new flow in a separate code branch for new instances.</item>
/// </list>
/// The state you see here (the compensation list, the steps) is never stored explicitly: replay rebuilds it.
/// That is why it can even contain delegates.
/// </remarks>
public sealed partial class OrderSagaWorkflow
{
    // AZURE: the activity retry policy is durable. The delay between attempts is a timer recorded in the history, so a
    // retry survives a restart and a deployment. It complements Polly in the HTTP client rather than replacing it:
    // Polly absorbs short glitches within one attempt, while this policy rides out longer outages
    // (e.g. orders-api being deployed).
    private static readonly TaskOptions SendRetry = TaskOptions.FromRetryPolicy(new RetryPolicy(10, TimeSpan.FromSeconds(2), 2.0, TimeSpan.FromMinutes(1)));
    private static readonly TaskOptions OrdersRetry = TaskOptions.FromRetryPolicy(new RetryPolicy(8, TimeSpan.FromSeconds(5), 2.0, TimeSpan.FromMinutes(5)));
    private static readonly TaskOptions RecordRetry = TaskOptions.FromRetryPolicy(new RetryPolicy(5, TimeSpan.FromSeconds(2), 2.0, TimeSpan.FromSeconds(30)));

    private readonly TaskOrchestrationContext _context;
    private readonly ILogger _logger;
    private readonly List<Compensation> _compensations = [];
    private readonly List<SagaStepRecord> _steps = [];

    private OrderSagaInput _input = null!;
    private Task<SagaReply> _cancellation = null!;
    private DateTimeOffset _startedAt;
    private SagaStatus _status = SagaStatus.Running;
    private string? _currentStep;
    private string? _rejectionReason;

    public OrderSagaWorkflow(TaskOrchestrationContext context)
    {
        _context = context;
        _logger = context.CreateReplaySafeLogger<OrderSagaWorkflow>();
    }

    private string InstanceId => _context.InstanceId;

    private DateTimeOffset Now => new(_context.CurrentUtcDateTime, TimeSpan.Zero);

    /// <summary>The customer asked for cancellation (the event could have arrived at any moment since the start).</summary>
    private bool CancellationRequested => _cancellation.IsCompleted;

    public async Task<SagaOutcome> RunAsync()
    {
        _input = _context.GetInput<OrderSagaInput>() ?? throw new InvalidOperationException("Order saga was started without input.");
        _startedAt = Now;

        // AZURE: external events are buffered by the instance. A task created once at the beginning completes the moment
        // the cancellation request arrives, no matter what the saga is waiting for at that time. We check it between steps.
        _cancellation = _context.WaitForExternalEvent<SagaReply>(SagaEvents.CancellationRequested);
        await RecordAsync();

        // Step 1: stock reservation.
        var stock = await ExecuteStepAsync(
            SagaEvents.ReserveStock,
            SagaCommandFactory.ReserveStock(_input),
            _input.Timeouts.Stock,
            new Compensation(SagaEvents.ReleaseStock, (attempt, reason) => SagaCommandFactory.ReleaseStock(_input, reason, attempt)));

        if (stock is not { Succeeded: true })
        {
            return await AbortAsync(stock is null ? OrderRejectionReasons.InternalError : OrderRejectionReasons.OutOfStock);
        }

        if (CancellationRequested)
        {
            return await AbortAsync(OrderRejectionReasons.CancelledByCustomer);
        }

        // Step 2: payment authorization. The saga assigns the identifier so that it can void the authorization
        // even when it did not get a reply before the timeout.
        var paymentId = _context.NewGuid();
        var payment = await ExecuteStepAsync(
            SagaEvents.AuthorizePayment,
            SagaCommandFactory.AuthorizePayment(_input, paymentId),
            _input.Timeouts.Payment,
            VoidCompensation(paymentId));

        if (payment is not { Succeeded: true })
        {
            return await AbortAsync(payment is null ? OrderRejectionReasons.PaymentTimeout : OrderRejectionReasons.PaymentDeclined);
        }

        if (CancellationRequested)
        {
            return await AbortAsync(OrderRejectionReasons.CancelledByCustomer);
        }

        // Step 3: shipment from the warehouse that reserved the stock.
        var shipmentId = _context.NewGuid();
        var shipment = await ExecuteStepAsync(
            SagaEvents.CreateShipment,
            SagaCommandFactory.CreateShipment(_input, shipmentId, stock.WarehouseCode ?? "default"),
            _input.Timeouts.Shipment,
            new Compensation(SagaEvents.CancelShipment, (attempt, reason) => SagaCommandFactory.CancelShipment(_input, shipmentId, reason, attempt)));

        if (shipment is not { Succeeded: true })
        {
            return await AbortAsync(OrderRejectionReasons.ShipmentUnavailable);
        }

        if (CancellationRequested)
        {
            return await AbortAsync(OrderRejectionReasons.CancelledByCustomer);
        }

        // Step 4: capture. From now on the payment compensation is a refund, not releasing the hold. We swap it before
        // sending the command, because after a timeout we do not know whether the capture succeeded; Payments performs a
        // refund of an uncaptured payment as releasing the hold.
        ReplaceCompensation(SagaEvents.VoidPayment, RefundCompensation(paymentId));
        var capture = await ExecuteStepAsync(SagaEvents.CapturePayment, SagaCommandFactory.CapturePayment(_input, paymentId), _input.Timeouts.Capture, compensation: null);

        if (capture is not { Succeeded: true })
        {
            if (capture is { Succeeded: false })
            {
                // A clear refusal: the funds were not captured, releasing the hold is enough.
                ReplaceCompensation(SagaEvents.RefundPayment, VoidCompensation(paymentId));
            }

            return await AbortAsync(capture is null ? OrderRejectionReasons.InternalError : OrderRejectionReasons.PaymentDeclined);
        }

        if (CancellationRequested)
        {
            return await AbortAsync(OrderRejectionReasons.CancelledByCustomer);
        }

        // Step 5: confirmation in Orders (service-to-service HTTP with If-Match, performed by an activity).
        switch (await CallOrdersAsync(SagaActivityNames.ConfirmOrder, new ConfirmOrderInput(_input.OrderId, shipment.TrackingNumber)))
        {
            case OrdersCallOutcome.Applied or null:
                break;

            case OrdersCallOutcome.CancellationPending:
                // The cancellation request reached Orders before its event reached the saga.
                return await AbortAsync(OrderRejectionReasons.CancelledByCustomer);

            default:
                // The order was closed by someone else (e.g. back-office): undo the effects, do not change Orders.
                return await AbortAsync(OrderRejectionReasons.InternalError, notifyOrders: false);
        }

        _status = SagaStatus.Confirmed;
        _currentStep = null;
        await RecordAsync();
        return SagaOutcome.Confirmed;
    }

    /// <summary>
    /// Sends the step's command and waits for a reply or a timeout. Returns <c>null</c> when there is no reply.
    /// </summary>
    /// <remarks>
    /// WHY: the compensation goes on the list before the command is sent, not after success. Compensations in ShopFlow
    /// are state-tolerant: releasing a non-existent reservation or cancelling a non-existent shipment succeeds without
    /// effect. A timeout ("I do not know whether the step ran") is therefore handled the same way as success. The
    /// compensation is removed only after a clear refusal, because then we know there is nothing to undo.
    /// </remarks>
    private async Task<SagaReply?> ExecuteStepAsync(string step, OutgoingCommand command, TimeSpan timeout, Compensation? compensation)
    {
        _currentStep = step;
        if (compensation is not null)
        {
            _compensations.Add(compensation);
        }

        UpdateCustomStatus();
        var reply = await SendAndWaitAsync(step, command, timeout);

        if (reply is { Succeeded: false } && compensation is not null)
        {
            _compensations.Remove(compensation);
        }

        await RecordStepAsync(step, reply);
        return reply;
    }

    private async Task<SagaReply?> SendAndWaitAsync(string step, OutgoingCommand command, TimeSpan timeout)
    {
        try
        {
            await _context.CallActivityAsync(SagaActivityNames.SendCommand, command, SendRetry);
        }
        catch (TaskFailedException exception)
        {
            // All send attempts failed (Service Bus unavailable for longer than a few minutes).
            // We do not know whether any copy got through, so we treat this as no reply.
            LogSendFailed(_logger, InstanceId, step, exception.Message);
            return null;
        }

        return await WaitForReplyAsync(step, timeout);
    }

    /// <summary>
    /// Race: the reply event against a durable timer.
    /// </summary>
    /// <remarks>
    /// AZURE: the loser of the race must be cancelled. An uncancelled durable timer keeps the instance alive: the
    /// orchestration will not finish until the timer fires (here up to 15 minutes after success), and in the dashboard it
    /// looks stuck. A timer uses no resources while waiting: it is an entry in the Durable Task Scheduler's schedule, not
    /// a sleeping thread.
    /// </remarks>
    private async Task<SagaReply?> WaitForReplyAsync(string eventName, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource();
        var reply = _context.WaitForExternalEvent<SagaReply>(eventName, cancellation.Token);
        var timer = _context.CreateTimer(_context.CurrentUtcDateTime.Add(timeout), cancellation.Token);

        var winner = await Task.WhenAny(reply, timer);
        cancellation.Cancel();

        return winner == reply ? await reply : null;
    }

    /// <summary>Undoes the completed steps in reverse order, then rejects or cancels the order.</summary>
    private async Task<SagaOutcome> AbortAsync(string reasonCode, bool notifyOrders = true)
    {
        _status = SagaStatus.Compensating;
        _rejectionReason = reasonCode;
        await RecordAsync();

        IReadOnlyList<Compensation> pending = [.. Enumerable.Reverse(_compensations)];
        var attempt = 1;

        while (pending.Count > 0)
        {
            pending = await RunCompensationsAsync(pending, reasonCode, attempt);
            if (pending.Count == 0)
            {
                break;
            }

            var resolution = await EscalateAsync($"compensation failed: {string.Join(", ", pending.Select(item => item.Step))}");
            if (resolution.Action == OperatorAction.MarkResolved)
            {
                break;
            }

            attempt++;
            _status = SagaStatus.Compensating;
        }

        var cancelled = reasonCode == OrderRejectionReasons.CancelledByCustomer;
        if (notifyOrders)
        {
            if (cancelled)
            {
                await CallOrdersAsync(SagaActivityNames.CompleteOrderCancellation, _input.OrderId);
            }
            else
            {
                await CallOrdersAsync(SagaActivityNames.RejectOrder, new RejectOrderInput(_input.OrderId, reasonCode, $"Order saga {InstanceId} aborted."));
            }
        }

        _status = cancelled ? SagaStatus.Cancelled : SagaStatus.Rejected;
        _currentStep = null;
        await RecordAsync();
        return cancelled ? SagaOutcome.Cancelled : SagaOutcome.Rejected;
    }

    /// <remarks>
    /// WHY: compensations run one after another, not in parallel. They are independent and a fan-out would be faster, but
    /// the "latest step first" order is easier to follow in the history and in the runbook, and a few seconds' difference
    /// for an aborted order has no business significance.
    /// </remarks>
    private async Task<IReadOnlyList<Compensation>> RunCompensationsAsync(IReadOnlyList<Compensation> pending, string reason, int attempt)
    {
        var failed = new List<Compensation>();

        foreach (var compensation in pending)
        {
            _currentStep = compensation.Step;
            UpdateCustomStatus();

            var reply = await SendAndWaitAsync(compensation.Step, compensation.Build(attempt, reason), _input.Timeouts.Compensation);
            await RecordStepAsync(compensation.Step, reply);

            if (reply is not { Succeeded: true })
            {
                failed.Add(compensation);
            }
        }

        return failed;
    }

    /// <summary>Operation on an order in orders-api; escalates to an operator after the retries are exhausted.</summary>
    /// <returns>The operation's result, or <c>null</c> when the operator marked the case as resolved manually.</returns>
    private async Task<OrdersCallOutcome?> CallOrdersAsync(string activity, object input)
    {
        while (true)
        {
            try
            {
                var outcome = await _context.CallActivityAsync<OrdersCallOutcome>(activity, input, OrdersRetry);
                await RecordStepAsync(activity, outcome.ToString(), detail: null);
                return outcome;
            }
            catch (TaskFailedException exception)
            {
                LogOrdersCallFailed(_logger, InstanceId, activity, exception.Message);

                var resolution = await EscalateAsync($"{activity} failed");
                if (resolution.Action == OperatorAction.MarkResolved)
                {
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// The saga cannot get out of the situation on its own: it stops and waits for a human decision.
    /// </summary>
    /// <remarks>
    /// AZURE: waiting for an event without a timer can last for days and costs nothing: the instance does not occupy a
    /// process and the Flex Consumption plan does not bill execution time. An Azure Monitor alert reacts to the
    /// "requires manual intervention" log entry (infra/modules/operations/alerts.bicep), and the operator answers through
    /// POST /api/sagas/{orderId}/resolution (docs/runbooks/payment-failure.md).
    /// </remarks>
    private async Task<OperatorResolution> EscalateAsync(string reason)
    {
        _status = SagaStatus.RequiresManualIntervention;
        _currentStep = reason;
        await RecordAsync();
        LogManualIntervention(_logger, InstanceId, _input.OrderId, reason);

        var resolution = await _context.WaitForExternalEvent<OperatorResolution>(SagaEvents.OperatorResolution);
        _steps.Add(new SagaStepRecord("Operator", resolution.Action.ToString(), Now, $"{resolution.Operator}: {resolution.Note}"));
        return resolution;
    }

    private Compensation VoidCompensation(Guid paymentId) =>
        new(SagaEvents.VoidPayment, (attempt, reason) => SagaCommandFactory.VoidPayment(_input, paymentId, reason, attempt));

    private Compensation RefundCompensation(Guid paymentId) =>
        new(SagaEvents.RefundPayment, (attempt, reason) => SagaCommandFactory.RefundPayment(_input, paymentId, reason, attempt));

    private void ReplaceCompensation(string step, Compensation replacement)
    {
        var index = _compensations.FindIndex(item => item.Step == step);
        if (index >= 0)
        {
            _compensations[index] = replacement;
        }
    }

    private Task RecordStepAsync(string step, SagaReply? reply) =>
        RecordStepAsync(step, reply is null ? "timeout" : reply.Succeeded ? "succeeded" : "failed", reply?.ReasonCode);

    private async Task RecordStepAsync(string step, string outcome, string? detail)
    {
        _steps.Add(new SagaStepRecord(step, outcome, Now, detail));
        await RecordAsync();
    }

    /// <summary>
    /// Saves the progress projection (Cosmos DB, saga-state container) and the instance status.
    /// </summary>
    /// <remarks>
    /// WHY: the projection is auxiliary (for support and diagnostics), so its failure must not stop the saga.
    /// The source of truth about the flow is the orchestration history in the Durable Task Scheduler.
    /// </remarks>
    private async Task RecordAsync()
    {
        UpdateCustomStatus();
        var progress = new SagaProgress(_input.OrderId, InstanceId, _status, _currentStep, _rejectionReason, [.. _steps], _startedAt, Now);

        try
        {
            await _context.CallActivityAsync(SagaActivityNames.RecordProgress, progress, RecordRetry);
        }
        catch (TaskFailedException exception)
        {
            LogProgressNotRecorded(_logger, InstanceId, exception.Message);
        }
    }

    /// <summary>
    /// AZURE: the custom status is saved together with the instance state, without calling an activity. It is visible in
    /// the Durable Task Scheduler dashboard and in the instance status GET, so an operator immediately sees where the saga is.
    /// </summary>
    private void UpdateCustomStatus() => _context.SetCustomStatus(new SagaCustomStatus(_status, _currentStep));

    private sealed record Compensation(string Step, Func<int, string, OutgoingCommand> Build);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Order saga {InstanceId} for order {OrderId} requires manual intervention: {Reason}")]
    private static partial void LogManualIntervention(ILogger logger, string instanceId, Guid orderId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Order saga {InstanceId} could not send {Step}: {Error}")]
    private static partial void LogSendFailed(ILogger logger, string instanceId, string step, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Order saga {InstanceId}: {Activity} failed after all retries: {Error}")]
    private static partial void LogOrdersCallFailed(ILogger logger, string instanceId, string activity, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Order saga {InstanceId}: progress projection was not recorded: {Error}")]
    private static partial void LogProgressNotRecorded(ILogger logger, string instanceId, string error);
}
