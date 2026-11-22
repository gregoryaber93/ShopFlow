namespace ShopFlow.Resilience;

public static class ShopFlowHttpHeaders
{
    /// <summary>
    /// Idempotency key assigned by the client. The same key with the same body means the same request.
    /// </summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>Correlation identifier of a business flow, passed between services.</summary>
    public const string CorrelationId = "X-Correlation-Id";

    /// <summary>Set on a response replayed from the idempotency store.</summary>
    public const string IdempotentReplayed = "Idempotent-Replayed";
}
