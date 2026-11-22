using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Payments.Domain;

public enum PaymentStatus
{
    /// <summary>Authorization requested, waiting for customer action (3-D Secure) and the provider's webhook.</summary>
    Pending = 0,
    Authorized = 1,
    Declined = 2,
    Captured = 3,
    Voided = 4,
    Refunded = 5,
}

public enum TransactionKind
{
    Authorize = 0,
    Capture = 1,
    Void = 2,
    Refund = 3,
}

/// <summary>One operation at the payment provider, recorded for accounting reconciliation and disputes.</summary>
public sealed record PaymentTransaction(TransactionKind Kind, string Outcome, Money Amount, string? ProviderReference, DateTimeOffset At);

public sealed record InvoiceInfo(string Number, string BlobPath, DateTimeOffset IssuedAt);

public sealed record PaymentAuthorizedDomainEvent(Guid PaymentId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PaymentDeclinedDomainEvent(Guid PaymentId, string DeclineCode, string? Reason, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PaymentCapturedDomainEvent(Guid PaymentId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PaymentCaptureFailedDomainEvent(Guid PaymentId, string ReasonCode, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PaymentVoidedDomainEvent(Guid PaymentId, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record PaymentRefundedDomainEvent(Guid PaymentId, Money Amount, DateTimeOffset OccurredAt) : IDomainEvent;

public sealed record InvoiceIssuedDomainEvent(Guid PaymentId, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>Authorization result from the payment provider, translated into domain language.</summary>
public enum AuthorizationOutcome
{
    Authorized = 0,
    Declined = 1,

    /// <summary>The provider requires additional customer authentication (3-D Secure); the outcome will come by webhook.</summary>
    RequiresAction = 2,
}

public sealed record AuthorizationResult(
    AuthorizationOutcome Outcome,
    string? ProviderReference,
    string? DeclineCode,
    string? DeclineReason,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Payment for one order. Every transition is idempotent, because commands and webhooks arrive at least once.
/// </summary>
/// <remarks>
/// The aggregate does not call the payment provider: it accepts the results of operations performed by the application
/// layer. Rules (e.g. "do not capture after the authorization has expired") can therefore be tested without a network,
/// and a retry after a save error does not require undoing anything in the domain.
/// </remarks>
public sealed class Payment : AggregateRoot<Guid>
{
    private readonly List<PaymentTransaction> _transactions;

    private Payment(Guid id, Guid orderId, Guid customerId, Money amount, PaymentStatus status, DateTimeOffset createdAt, List<PaymentTransaction> transactions)
        : base(id)
    {
        OrderId = orderId;
        CustomerId = customerId;
        Amount = amount;
        Status = status;
        CreatedAt = createdAt;
        _transactions = transactions;
    }

    public Guid OrderId { get; }

    public Guid CustomerId { get; }

    public Money Amount { get; }

    public PaymentStatus Status { get; private set; }

    public string? ProviderReference { get; private set; }

    public DateTimeOffset? AuthorizationExpiresAt { get; private set; }

    public string? DeclineCode { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public InvoiceInfo? Invoice { get; private set; }

    public IReadOnlyList<PaymentTransaction> Transactions => _transactions;

    public static Payment Authorize(Guid paymentId, Guid orderId, Guid customerId, Money amount, AuthorizationResult result, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);

        var payment = new Payment(paymentId, orderId, customerId, amount, PaymentStatus.Pending, now, []);
        payment.ApplyAuthorization(result, now);
        return payment;
    }

    /// <summary>
    /// Tombstone payment: a void arrived for a payment that was never authorized.
    /// </summary>
    /// <remarks>
    /// WHY: the payments-commands queue has sessions, so the commands of one order arrive in the order they were sent and
    /// "void before authorization" does not happen in the normal flow (unlike in Inventory). The tombstone protects against
    /// an abnormal case: an AuthorizePayment replayed from the DLQ after the saga finished would hold the customer's funds
    /// for a week, until the authorization expires.
    /// </remarks>
    public static Payment VoidedTombstone(Guid paymentId, Guid orderId, Guid customerId, DateTimeOffset now)
    {
        var payment = new Payment(paymentId, orderId, customerId, Money.Zero("PLN"), PaymentStatus.Voided, now, []) { UpdatedAt = now };
        payment.Raise(new PaymentVoidedDomainEvent(paymentId, now));
        return payment;
    }

    /// <summary>The 3-D Secure outcome delivered by the provider's webhook.</summary>
    public void CompleteAuthorization(AuthorizationResult result, DateTimeOffset now)
    {
        if (Status != PaymentStatus.Pending)
        {
            return;
        }

        ApplyAuthorization(result, now);
    }

    public bool IsAuthorizationExpired(DateTimeOffset now) => AuthorizationExpiresAt is { } expiresAt && expiresAt <= now;

    public void MarkCaptured(string? providerReference, DateTimeOffset now)
    {
        if (Status == PaymentStatus.Captured)
        {
            return;
        }

        Record(TransactionKind.Capture, "succeeded", Amount, providerReference, now);
        Status = PaymentStatus.Captured;
        Raise(new PaymentCapturedDomainEvent(Id, now));
    }

    public void MarkCaptureFailed(string reasonCode, DateTimeOffset now)
    {
        Record(TransactionKind.Capture, reasonCode, Amount, ProviderReference, now);
        Raise(new PaymentCaptureFailedDomainEvent(Id, reasonCode, now));
    }

    /// <summary>Releases the hold on funds (the authorization). Idempotent.</summary>
    public void MarkVoided(DateTimeOffset now)
    {
        if (Status is not (PaymentStatus.Pending or PaymentStatus.Authorized))
        {
            return;
        }

        Record(TransactionKind.Void, "succeeded", Amount, ProviderReference, now);
        Status = PaymentStatus.Voided;
        Raise(new PaymentVoidedDomainEvent(Id, now));
    }

    /// <summary>Refunds captured funds. Idempotent.</summary>
    public void MarkRefunded(DateTimeOffset now)
    {
        if (Status != PaymentStatus.Captured)
        {
            return;
        }

        Record(TransactionKind.Refund, "succeeded", Amount, ProviderReference, now);
        Status = PaymentStatus.Refunded;
        Raise(new PaymentRefundedDomainEvent(Id, Amount, now));
    }

    /// <summary>The invoice appeared in Blob Storage. Idempotent for the same invoice.</summary>
    public void AttachInvoice(string number, string blobPath, DateTimeOffset now)
    {
        if (Invoice?.Number == number)
        {
            return;
        }

        Invoice = new InvoiceInfo(number, blobPath, now);
        UpdatedAt = now;
        Raise(new InvoiceIssuedDomainEvent(Id, now));
    }

    public static Payment Restore(
        Guid id,
        Guid orderId,
        Guid customerId,
        Money amount,
        PaymentStatus status,
        string? providerReference,
        DateTimeOffset? authorizationExpiresAt,
        string? declineCode,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        InvoiceInfo? invoice,
        IReadOnlyList<PaymentTransaction> transactions,
        string? concurrencyToken)
    {
        var payment = new Payment(id, orderId, customerId, amount, status, createdAt, [.. transactions])
        {
            ProviderReference = providerReference,
            AuthorizationExpiresAt = authorizationExpiresAt,
            DeclineCode = declineCode,
            UpdatedAt = updatedAt,
            Invoice = invoice,
        };

        payment.SetConcurrencyToken(concurrencyToken);
        return payment;
    }

    private void ApplyAuthorization(AuthorizationResult result, DateTimeOffset now)
    {
        ProviderReference = result.ProviderReference ?? ProviderReference;
        Record(TransactionKind.Authorize, result.Outcome.ToString(), Amount, result.ProviderReference, now);

        switch (result.Outcome)
        {
            case AuthorizationOutcome.Authorized:
                Status = PaymentStatus.Authorized;
                AuthorizationExpiresAt = result.ExpiresAt;
                Raise(new PaymentAuthorizedDomainEvent(Id, now));
                break;

            case AuthorizationOutcome.Declined:
                Status = PaymentStatus.Declined;
                DeclineCode = result.DeclineCode ?? "declined";
                Raise(new PaymentDeclinedDomainEvent(Id, DeclineCode, result.DeclineReason, now));
                break;

            case AuthorizationOutcome.RequiresAction:
                // No event: the saga keeps waiting (up to 15 minutes), and the webhook will bring the result.
                Status = PaymentStatus.Pending;
                break;
        }
    }

    private void Record(TransactionKind kind, string outcome, Money amount, string? providerReference, DateTimeOffset now)
    {
        _transactions.Add(new PaymentTransaction(kind, outcome, amount, providerReference, now));
        UpdatedAt = now;
    }
}
