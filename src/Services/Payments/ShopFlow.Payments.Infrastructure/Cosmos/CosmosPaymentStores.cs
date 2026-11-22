using System.Globalization;
using System.Net;
using Microsoft.Azure.Cosmos;
using ShopFlow.Payments.Application.Abstractions;
using ShopFlow.Payments.Domain;
using ShopFlow.Persistence.Cosmos;

namespace ShopFlow.Payments.Infrastructure.Cosmos;

internal sealed class CosmosPaymentRepository(PaymentsContainers containers, ICosmosUnitOfWorkFactory unitOfWork) : IPaymentRepository
{
    public async Task<Payment?> GetAsync(Guid orderId, Guid paymentId, CancellationToken cancellationToken)
    {
        var document = await containers.Payments
            .ReadOrDefaultAsync<PaymentDocument>(paymentId.ToString("D"), orderId.ToString("D"), cancellationToken)
            .ConfigureAwait(false);

        return document?.ToDomain();
    }

    public async Task SaveAsync(Payment payment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payment);

        var document = PaymentDocument.From(payment);
        var work = unitOfWork.Create(containers.Payments, PaymentsContainers.PartitionKey, document.OrderId);
        work = payment.ConcurrencyToken is { } etag ? work.Replace(document, etag) : work.Create(document);

        var result = await work.CommitAsync(cancellationToken).ConfigureAwait(false);
        payment.SetConcurrencyToken(result.GetETag(document.Id) ?? payment.ConcurrencyToken);
    }
}

/// <summary>
/// Invoice numbers consecutive and without gaps within a month: <c>FV/2026/11/000123</c>.
/// </summary>
/// <remarks>
/// WHY: regulations require continuous invoice numbering, so a random number (like the order number in Orders) is not
/// an option. A counter is the classic "hot document", which is why it is split by month and limited to invoices, of
/// which there are as many as captured payments, i.e. a few per second at peak.
/// <para>
/// AZURE: allocation is atomic thanks to a TransactionalBatch in the synthetic <c>invoice-numbers:{yyyy-MM}</c>
/// partition: in one batch we replace the counter (with If-Match) and create an allocation document whose identifier is
/// the order identifier. Either both writes succeed or neither does, so a number is never "lost". Two concurrent
/// allocations end with 412 on the counter or 409 on the allocation; the loser retries and gets the next number or
/// reads the one already allocated.
/// </para>
/// <para>
/// Limit: one logical partition has limited write throughput, and every collision is a retry. With hundreds of invoices
/// per second a dedicated number generator (e.g. a sequence in Azure SQL) would be better.
/// </para>
/// </remarks>
internal sealed class CosmosInvoiceNumberAllocator(PaymentsContainers containers) : IInvoiceNumberAllocator
{
    private const int MaxAttempts = 10;

    public async Task<string> AllocateAsync(Guid orderId, DateTimeOffset issuedAt, CancellationToken cancellationToken)
    {
        var container = containers.Payments;
        var partition = $"invoice-numbers:{issuedAt.ToString("yyyy-MM", CultureInfo.InvariantCulture)}";
        var allocationId = orderId.ToString("D");

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var existing = await container.ReadOrDefaultAsync<InvoiceAllocationDocument>(allocationId, partition, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return existing.Number;
            }

            var counter = await container.ReadOrDefaultAsync<InvoiceCounterDocument>(InvoiceCounterDocument.CounterId, partition, cancellationToken).ConfigureAwait(false);
            var next = (counter?.LastNumber ?? 0) + 1;
            var number = string.Create(CultureInfo.InvariantCulture, $"FV/{issuedAt:yyyy}/{issuedAt:MM}/{next:D6}");

            var updatedCounter = new InvoiceCounterDocument { Id = InvoiceCounterDocument.CounterId, OrderId = partition, LastNumber = next };
            var allocation = new InvoiceAllocationDocument { Id = allocationId, OrderId = partition, AllocatedTo = orderId, Number = number };

            var batch = container.CreateTransactionalBatch(new PartitionKey(partition));
            batch = counter is null
                ? batch.CreateItem(updatedCounter)
                : batch.ReplaceItem(updatedCounter.Id, updatedCounter, new TransactionalBatchItemRequestOptions { IfMatchEtag = counter.ETag });
            batch.CreateItem(allocation);

            using var response = await batch.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            CosmosTelemetry.RecordRequestCharge(container.Id, "invoice_number_allocation", response.RequestCharge);

            if (response.IsSuccessStatusCode)
            {
                return number;
            }

            if (response.StatusCode is not (HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed))
            {
                throw new CosmosBatchException(response.StatusCode, response.ErrorMessage, response.RetryAfter, response.RequestCharge);
            }
        }

        throw new InvalidOperationException($"Could not allocate an invoice number for order {orderId} after {MaxAttempts} attempts (contention on {partition}).");
    }
}
