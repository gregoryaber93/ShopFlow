using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using ShopFlow.Payments.Domain;
using ShopFlow.Persistence.Cosmos.Documents;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Payments.Infrastructure.Cosmos;

/// <summary>Containers of the <c>payments</c> database. The structure matches infra/modules/data/cosmos-database.bicep.</summary>
public sealed class PaymentsContainers(Database database)
{
    public const string DatabaseName = "payments";
    public const string PaymentsName = "payments";
    public const string PartitionKey = "orderId";
    public const string LeasesName = "leases";

    /// <summary>
    /// Partition = order: the payment, its transactions, outbox and inbox. The same container also holds invoice number
    /// counters in the synthetic "invoice-numbers:{yyyy-MM}" partitions.
    /// </summary>
    public Container Payments { get; } = database.GetContainer(PaymentsName);
}

public sealed class PaymentDocument : CosmosDocument
{
    public const string DocumentType = "payment";

    [JsonPropertyName("docType")]
    public override string DocType => DocumentType;

    public required string OrderId { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public required Guid CustomerId { get; init; }

    public required MoneyDocument Amount { get; init; }

    public required PaymentStatus Status { get; init; }

    public string? ProviderReference { get; init; }

    public DateTimeOffset? AuthorizationExpiresAt { get; init; }

    public string? DeclineCode { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public InvoiceDocumentInfo? Invoice { get; init; }

    public List<TransactionDocument> Transactions { get; init; } = [];

    public static PaymentDocument From(Payment payment) => new()
    {
        Id = payment.Id.ToString("D"),
        OrderId = payment.OrderId.ToString("D"),
        CustomerId = payment.CustomerId,
        Amount = new MoneyDocument(payment.Amount.Amount, payment.Amount.Currency),
        Status = payment.Status,
        ProviderReference = payment.ProviderReference,
        AuthorizationExpiresAt = payment.AuthorizationExpiresAt,
        DeclineCode = payment.DeclineCode,
        CreatedAt = payment.CreatedAt,
        UpdatedAt = payment.UpdatedAt,
        Invoice = payment.Invoice is { } invoice ? new InvoiceDocumentInfo(invoice.Number, invoice.BlobPath, invoice.IssuedAt) : null,
        Transactions =
        [
            .. payment.Transactions.Select(transaction => new TransactionDocument(
                transaction.Kind,
                transaction.Outcome,
                new MoneyDocument(transaction.Amount.Amount, transaction.Amount.Currency),
                transaction.ProviderReference,
                transaction.At)),
        ],
    };

    public Payment ToDomain() => Payment.Restore(
        Guid.Parse(Id),
        Guid.Parse(OrderId),
        CustomerId,
        new Money(Amount.Amount, Amount.Currency),
        Status,
        ProviderReference,
        AuthorizationExpiresAt,
        DeclineCode,
        CreatedAt,
        UpdatedAt,
        Invoice is { } invoice ? new InvoiceInfo(invoice.Number, invoice.BlobPath, invoice.IssuedAt) : null,
        [.. Transactions.Select(transaction => new PaymentTransaction(
            transaction.Kind,
            transaction.Outcome,
            new Money(transaction.Amount.Amount, transaction.Amount.Currency),
            transaction.ProviderReference,
            transaction.At))],
        ETag);
}

public sealed record MoneyDocument(decimal Amount, string Currency);

public sealed record InvoiceDocumentInfo(string Number, string BlobPath, DateTimeOffset IssuedAt);

public sealed record TransactionDocument(TransactionKind Kind, string Outcome, MoneyDocument Amount, string? ProviderReference, DateTimeOffset At);

/// <summary>Invoice counter for a period (month). One document per "invoice-numbers:{yyyy-MM}" partition.</summary>
public sealed class InvoiceCounterDocument : CosmosDocument
{
    public const string DocumentType = "invoice-counter";
    public const string CounterId = "counter";

    [JsonPropertyName("docType")]
    public override string DocType => DocumentType;

    /// <summary>Synthetic partition key value (the field is called orderId because that is the container's key).</summary>
    public required string OrderId { get; init; }

    public required int LastNumber { get; init; }
}

/// <summary>Number allocation for an order; a retry for the same order reads it instead of taking a new number.</summary>
public sealed class InvoiceAllocationDocument : CosmosDocument
{
    public const string DocumentType = "invoice-allocation";

    [JsonPropertyName("docType")]
    public override string DocType => DocumentType;

    public required string OrderId { get; init; }

    public required Guid AllocatedTo { get; init; }

    public required string Number { get; init; }
}
