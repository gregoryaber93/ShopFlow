using ShopFlow.Contracts.Common;

namespace ShopFlow.Contracts.Payments.V1;

[MessageContract("PaymentAuthorized", 1)]
public sealed record PaymentAuthorized : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid PaymentId { get; init; }

    public required MoneyDto Amount { get; init; }

    /// <summary>Transaction identifier on the payment provider's side, for reconciliation and disputes.</summary>
    public required string ProviderReference { get; init; }

    public required DateTimeOffset AuthorizedAt { get; init; }

    /// <summary>After this moment the provider releases the hold on its own and capture is no longer possible.</summary>
    public required DateTimeOffset AuthorizationExpiresAt { get; init; }
}

[MessageContract("PaymentDeclined", 1)]
public sealed record PaymentDeclined : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public Guid? PaymentId { get; init; }

    /// <summary>Decline code normalized by Payments, e.g. <c>insufficient_funds</c>, <c>card_expired</c>.</summary>
    public required string DeclineCode { get; init; }

    public string? DeclineReason { get; init; }

    public required DateTimeOffset DeclinedAt { get; init; }
}

[MessageContract("PaymentCaptured", 1)]
public sealed record PaymentCaptured : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid PaymentId { get; init; }

    public required MoneyDto Amount { get; init; }

    public required string ProviderReference { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }
}

[MessageContract("PaymentCaptureFailed", 1)]
public sealed record PaymentCaptureFailed : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid PaymentId { get; init; }

    /// <summary><c>authorization_expired</c>, <c>provider_rejected</c>.</summary>
    public required string ReasonCode { get; init; }

    public required DateTimeOffset FailedAt { get; init; }
}

[MessageContract("PaymentVoided", 1)]
public sealed record PaymentVoided : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid PaymentId { get; init; }

    public required DateTimeOffset VoidedAt { get; init; }
}

[MessageContract("PaymentRefunded", 1)]
public sealed record PaymentRefunded : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required Guid PaymentId { get; init; }

    public required MoneyDto Amount { get; init; }

    public required DateTimeOffset RefundedAt { get; init; }
}

/// <summary>The invoice was registered after its PDF appeared in Blob Storage.</summary>
[MessageContract("InvoiceIssued", 1)]
public sealed record InvoiceIssued : IIntegrationEvent, IOrderScoped
{
    public required Guid OrderId { get; init; }

    public required Guid CustomerId { get; init; }

    public required string InvoiceNumber { get; init; }

    public required MoneyDto Total { get; init; }

    /// <summary>
    /// Blob path relative to the container, e.g. <c>2026/11/{orderId}/FV-2026-11-000123.pdf</c>.
    /// </summary>
    /// <remarks>
    /// AZURE: never a full URL with a SAS token. The event lives in subscriptions, the DLQ and the
    /// Event Hubs archive longer than any reasonable SAS lifetime, and anyone who reads it would get
    /// access to the file. The receiver generates a short-lived user delegation SAS itself.
    /// </remarks>
    public required string BlobPath { get; init; }

    public required DateTimeOffset IssuedAt { get; init; }
}
