using ShopFlow.Payments.Domain;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Payments.Application.Abstractions;

/// <summary>Saves the payment together with the current operation's outbox and inbox (one TransactionalBatch in the order's partition).</summary>
public interface IPaymentRepository
{
    Task<Payment?> GetAsync(Guid orderId, Guid paymentId, CancellationToken cancellationToken);

    /// <summary>Creates (when the payment has no version token) or conditionally replaces.</summary>
    Task SaveAsync(Payment payment, CancellationToken cancellationToken);
}

public sealed record AuthorizationRequest(Guid PaymentId, Guid OrderId, Money Amount, string PaymentMethodToken, bool ForceThreeDSecure);

public sealed record GatewayOperationResult(bool Succeeded, string? ProviderReference, string? FailureCode)
{
    public static GatewayOperationResult Success(string? providerReference) => new(true, providerReference, null);

    public static GatewayOperationResult Failure(string failureCode) => new(false, null, failureCode);
}

/// <summary>
/// Port to the payment service provider (PSP).
/// </summary>
/// <remarks>
/// Contract for implementations: every operation sends an idempotency key derived from the payment identifier and the
/// operation type. A business refusal (card declined, authorization expired) is a result, not an exception.
/// An exception means "we do not know what happened" (timeout, 5xx) and causes the whole message to be retried.
/// </remarks>
public interface IPaymentGateway
{
    Task<AuthorizationResult> AuthorizeAsync(AuthorizationRequest request, CancellationToken cancellationToken);

    Task<GatewayOperationResult> CaptureAsync(Guid paymentId, string providerReference, Money amount, CancellationToken cancellationToken);

    Task<GatewayOperationResult> VoidAsync(Guid paymentId, string providerReference, CancellationToken cancellationToken);

    Task<GatewayOperationResult> RefundAsync(Guid paymentId, string providerReference, Money amount, CancellationToken cancellationToken);
}

/// <summary>
/// Technical error of the payment provider.
/// </summary>
/// <param name="code">Provider or HTTP error code.</param>
/// <param name="isPermanent">
/// <c>true</c> when retrying makes no sense, e.g. 409 "same idempotency key with different parameters".
/// That means a bug on our side, not a temporary outage.
/// </param>
public sealed class PaymentGatewayException(string code, bool isPermanent, string message) : Exception(message)
{
    public string Code { get; } = code;

    public bool IsPermanent { get; } = isPermanent;
}

public interface IPaymentFeatures
{
    /// <summary>Force 3-D Secure for every transaction (e.g. during an attack with stolen cards).</summary>
    Task<bool> ForceThreeDSecureAsync();
}

public static class PaymentFeatureFlags
{
    public const string ForceThreeDSecure = "Payments.ForceThreeDSecure";
}

public sealed record InvoiceDocument(string Number, Guid OrderId, Guid CustomerId, Guid PaymentId, Money Amount, DateTimeOffset IssuedAt);

public sealed record StoredInvoice(string BlobPath, Guid OrderId, Guid PaymentId, string InvoiceNumber);

/// <summary>Consecutive invoice numbers without gaps, assigned idempotently per order.</summary>
public interface IInvoiceNumberAllocator
{
    Task<string> AllocateAsync(Guid orderId, DateTimeOffset issuedAt, CancellationToken cancellationToken);
}

public interface IInvoiceRenderer
{
    byte[] Render(InvoiceDocument invoice);
}

public interface IInvoiceStorage
{
    /// <summary>Saves the PDF if it does not exist yet. Returns the blob path relative to the container.</summary>
    Task<string> UploadAsync(InvoiceDocument invoice, byte[] pdf, CancellationToken cancellationToken);

    /// <summary>Invoice metadata stored on the blob; <c>null</c> when the blob lacks the required metadata.</summary>
    Task<StoredInvoice?> ReadAsync(Uri blobUrl, CancellationToken cancellationToken);
}
