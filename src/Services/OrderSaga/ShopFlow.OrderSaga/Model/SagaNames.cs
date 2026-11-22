namespace ShopFlow.OrderSaga.Model;

public static class OrderSagaInstance
{
    public const string OrchestratorName = "OrderSaga";

    /// <summary>
    /// Instance identifier derived from the order identifier.
    /// </summary>
    /// <remarks>
    /// WHY: a deterministic identifier is the first line of defence against two sagas for one order. A redelivered
    /// OrderSubmitted lands on the already existing instance, and participant replies know which instance they belong to
    /// without any mapping table.
    /// </remarks>
    public static string IdFor(Guid orderId) => $"order-{orderId:D}";
}

/// <summary>
/// Names of the orchestration's external events. A reply event is named after the command it answers, so the
/// orchestrator waits for "AuthorizePayment" after sending AuthorizePayment.
/// </summary>
/// <remarks>
/// WHY: one event per command, not per participant (e.g. "PaymentReply"). Payments answers four different commands.
/// With a shared name a late duplicate of PaymentAuthorized could be taken as the reply to a later CapturePayment.
/// With separate names the duplicate waits in the instance's event buffer and nobody ever receives it.
/// </remarks>
public static class SagaEvents
{
    public const string ReserveStock = "ReserveStock";
    public const string AuthorizePayment = "AuthorizePayment";
    public const string CreateShipment = "CreateShipment";
    public const string CapturePayment = "CapturePayment";

    public const string ReleaseStock = "ReleaseStock";
    public const string VoidPayment = "VoidPayment";
    public const string RefundPayment = "RefundPayment";
    public const string CancelShipment = "CancelShipment";

    public const string CancellationRequested = "CancellationRequested";
    public const string OperatorResolution = "OperatorResolution";
}

public static class SagaActivityNames
{
    public const string SendCommand = "SendSagaCommand";
    public const string ConfirmOrder = "ConfirmOrder";
    public const string RejectOrder = "RejectOrder";
    public const string CompleteOrderCancellation = "CompleteOrderCancellation";
    public const string RecordProgress = "RecordSagaProgress";
}
