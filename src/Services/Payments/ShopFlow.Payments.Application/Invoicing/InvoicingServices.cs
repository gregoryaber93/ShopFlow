using Microsoft.Extensions.Logging;
using ShopFlow.Contracts.Payments.V1;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Failures;
using ShopFlow.Messaging.Handling;
using ShopFlow.Payments.Application.Abstractions;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Payments.Application.Invoicing;

/// <summary>
/// Choreography: after the capture, generates a PDF invoice and stores it in Blob Storage.
/// Payments listens to its own event here (the payments-events/invoicing subscription).
/// </summary>
/// <remarks>
/// WHY: the invoice is outside the saga. The saga is about whether the order goes through; the invoice is a consequence
/// that needs no compensation and can wait (e.g. when Blob Storage has a brief outage).
/// <para>
/// This handler does not publish InvoiceIssued. Writing a file to Blob Storage and publishing a message cannot be
/// covered by one transaction. The store itself emits the BlobCreated event after a durable write, and Event Grid
/// delivers it to the invoice-blob-events queue (see <see cref="InvoiceRegistrationService"/>). Blob Storage with Event
/// Grid acts as an outbox for files here.
/// </para>
/// </remarks>
public sealed partial class InvoiceGenerationHandler(
    IInvoiceNumberAllocator numbers,
    IInvoiceRenderer renderer,
    IInvoiceStorage storage,
    ILogger<InvoiceGenerationHandler> logger) : IMessageHandler<PaymentCaptured>
{
    public async Task HandleAsync(PaymentCaptured message, MessageContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Idempotent per order: a repeat gets the same number and the file write does not overwrite an existing file.
        // That is why this handler needs no inbox.
        var number = await numbers.AllocateAsync(message.OrderId, message.CapturedAt, cancellationToken).ConfigureAwait(false);

        var invoice = new InvoiceDocument(
            number,
            message.OrderId,
            message.CustomerId,
            message.PaymentId,
            new Money(message.Amount.Amount, message.Amount.Currency),
            message.CapturedAt);

        var path = await storage.UploadAsync(invoice, renderer.Render(invoice), cancellationToken).ConfigureAwait(false);
        LogGenerated(number, message.OrderId, path);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Invoice {InvoiceNumber} for order {OrderId} stored at {BlobPath}")]
    private partial void LogGenerated(string invoiceNumber, Guid orderId, string blobPath);
}

/// <summary>
/// Registers the invoice after a BlobCreated event from Event Grid and publishes InvoiceIssued.
/// </summary>
/// <remarks>
/// Also works for invoices we did not generate ourselves, e.g. a correction uploaded manually by accounting to the
/// invoices container. Condition: the blob has orderId, paymentId and invoiceNumber metadata.
/// </remarks>
public sealed class InvoiceRegistrationService(
    IInvoiceStorage storage,
    IPaymentRepository payments,
    PaymentEventPublisher publisher,
    TimeProvider timeProvider)
{
    public async Task RegisterAsync(Uri blobUrl, CancellationToken cancellationToken)
    {
        var invoice = await storage.ReadAsync(blobUrl, cancellationToken).ConfigureAwait(false)
            ?? throw new PermanentMessageException(
                DeadLetterReasons.ValidationFailed,
                $"Blob {blobUrl} has no invoice metadata (orderId, paymentId, invoiceNumber).");

        var payment = await payments.GetAsync(invoice.OrderId, invoice.PaymentId, cancellationToken).ConfigureAwait(false)
            ?? throw new OutOfOrderMessageException($"Payment {invoice.PaymentId} for invoice {invoice.InvoiceNumber} is not visible yet.");

        payment.AttachInvoice(invoice.InvoiceNumber, invoice.BlobPath, timeProvider.GetUtcNow());
        publisher.Publish(payment);
        await payments.SaveAsync(payment, cancellationToken).ConfigureAwait(false);
    }
}
