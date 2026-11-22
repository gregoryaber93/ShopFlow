using System.ComponentModel.DataAnnotations;
using ShopFlow.Contracts.Common;

namespace ShopFlow.OrderSaga.Model;

/// <summary>
/// Orchestration input, recorded in the instance history at start.
/// </summary>
/// <remarks>
/// AZURE: Durable Functions stores the input in the orchestration history (Durable Task Scheduler) and provides it on
/// every replay. Everything the saga's flow depends on must be here or in activity results, not in configuration read
/// inside the orchestrator.
/// WHY: timeouts are part of the input and are not read from <see cref="SagaOptions"/> in the orchestrator. Changing
/// the configuration while an instance runs (e.g. shortening the payment timeout) would change the outcome of replaying
/// the history, and the orchestration would fail with a non-determinism error. An instance runs with the timeouts from
/// its start; new settings apply to new orders.
/// </remarks>
public sealed record OrderSagaInput
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string OrderNumber { get; init; }

    public required IReadOnlyList<StockLineDto> Lines { get; init; }

    public required MoneyDto Total { get; init; }

    public required SagaDelivery Delivery { get; init; }

    public required string PaymentMethodToken { get; init; }

    /// <summary>Correlation of the order flow; every command sent by the saga inherits it.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>MessageId of the OrderSubmitted event that started the saga (the commands' CausationId).</summary>
    public required string TriggeringMessageId { get; init; }

    public required SagaTimeouts Timeouts { get; init; }
}

public sealed record SagaDelivery(bool IsPickupPoint, AddressDto? Address, string? PickupPointId);

public sealed record SagaTimeouts(TimeSpan Stock, TimeSpan Payment, TimeSpan Shipment, TimeSpan Capture, TimeSpan Compensation);

/// <summary>
/// Normalized participant reply, delivered to the orchestration as an external event.
/// </summary>
public sealed record SagaReply
{
    public required bool Succeeded { get; init; }

    /// <summary>Type of the original message, e.g. <c>PaymentDeclined</c>.</summary>
    public required string ReplyType { get; init; }

    public string? ReasonCode { get; init; }

    public string? WarehouseCode { get; init; }

    public string? TrackingNumber { get; init; }
}

public enum OperatorAction
{
    /// <summary>Retry the compensations or the Orders operation that failed.</summary>
    Retry = 0,

    /// <summary>The operator fixed the situation manually (e.g. a refund in the payment provider's panel); the saga finishes.</summary>
    MarkResolved = 1,
}

public sealed record OperatorResolution(OperatorAction Action, string Operator, string? Note);

/// <summary>Orchestration outcome, visible as the instance output in the Durable Task Scheduler dashboard.</summary>
public enum SagaOutcome
{
    Confirmed = 0,
    Rejected = 1,
    Cancelled = 2,
}

public enum SagaStatus
{
    Running = 0,
    Compensating = 1,
    RequiresManualIntervention = 2,
    Confirmed = 3,
    Rejected = 4,
    Cancelled = 5,
}

/// <summary>Custom status of the instance, read by the dashboard and the operator endpoint.</summary>
public sealed record SagaCustomStatus(SagaStatus Status, string? Step);

public sealed record SagaStepRecord(string Step, string Outcome, DateTimeOffset At, string? Detail);

/// <summary>Business projection of the saga's progress saved in Cosmos DB (saga-state container).</summary>
public sealed record SagaProgress(
    Guid OrderId,
    string InstanceId,
    SagaStatus Status,
    string? CurrentStep,
    string? RejectionReason,
    IReadOnlyList<SagaStepRecord> Steps,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Result of an operation on an order in orders-api.</summary>
public enum OrdersCallOutcome
{
    Applied = 0,

    /// <summary>The customer asked for cancellation before the saga managed to confirm the order.</summary>
    CancellationPending = 1,

    /// <summary>The order is already closed (e.g. manually by back-office); the operation makes no sense.</summary>
    OrderClosed = 2,
}

public sealed record ConfirmOrderInput(Guid OrderId, string? TrackingNumber);

public sealed record RejectOrderInput(Guid OrderId, string ReasonCode, string? Description);

public sealed class SagaOptions
{
    public const string SectionName = "Saga";

    [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
    public TimeSpan StockTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Long, because it covers 3-D Secure authentication performed by a human.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "01:00:00")]
    public TimeSpan PaymentTimeout { get; set; } = TimeSpan.FromMinutes(15);

    [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
    public TimeSpan ShipmentTimeout { get; set; } = TimeSpan.FromMinutes(5);

    [Range(typeof(TimeSpan), "00:00:30", "01:00:00")]
    public TimeSpan CaptureTimeout { get; set; } = TimeSpan.FromMinutes(5);

    [Range(typeof(TimeSpan), "00:00:30", "06:00:00")]
    public TimeSpan CompensationTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public SagaTimeouts ToTimeouts() => new(StockTimeout, PaymentTimeout, ShipmentTimeout, CaptureTimeout, CompensationTimeout);
}
