using System.Text;
using ShopFlow.Contracts.Common;
using ShopFlow.Contracts.Payments.V1;
using ShopFlow.Messaging.Failures;
using ShopFlow.Payments.Application.Abstractions;
using ShopFlow.Payments.Application.Invoicing;
using ShopFlow.Payments.Infrastructure.Invoicing;
using ShopFlow.SharedKernel.Domain;

namespace ShopFlow.Payments.UnitTests;

public sealed class InvoiceTests : IDisposable
{
    private static readonly Guid OrderId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000f1");
    private static readonly Guid PaymentId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000f2");

    private readonly PaymentsTestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Captured_payment_gets_one_invoice_even_when_the_event_is_redelivered()
    {
        await _host.DispatchAsync(Captured());
        await _host.DispatchAsync(Captured(), Guid.NewGuid());

        var invoice = _host.Invoices.Blobs.Values.ShouldHaveSingleItem();
        invoice.InvoiceNumber.ShouldBe("FV/2026/11/000001");
        invoice.BlobPath.ShouldBe($"2026/11/{OrderId:D}/FV-2026-11-000001.pdf");
    }

    [Fact]
    public async Task Blob_created_event_attaches_the_invoice_and_announces_it_once()
    {
        await GivenAuthorizedAndCapturedAsync();
        await _host.DispatchAsync(Captured());
        var blobUrl = new Uri($"https://stshopflowdocsdevweu.blob.core.windows.net/invoices/{_host.Invoices.Blobs.Keys.Single()}");

        await RegisterAsync(blobUrl, eventId: Guid.NewGuid());
        await RegisterAsync(blobUrl, eventId: Guid.NewGuid());

        var issued = _host.Published<InvoiceIssued>().ShouldHaveSingleItem();
        issued.InvoiceNumber.ShouldBe("FV/2026/11/000001");
        issued.BlobPath.ShouldNotContain("sig=", Case.Insensitive);
    }

    [Fact]
    public async Task Blob_without_invoice_metadata_goes_to_the_dlq()
    {
        var exception = await Should.ThrowAsync<PermanentMessageException>(() =>
            RegisterAsync(new Uri("https://stshopflowdocsdevweu.blob.core.windows.net/invoices/manual/unknown.pdf"), Guid.NewGuid()));

        exception.Reason.ShouldBe(DeadLetterReasons.ValidationFailed);
    }

    [Fact]
    public void Rendered_pdf_has_a_valid_cross_reference_table()
    {
        var pdf = new SimplePdfInvoiceRenderer().Render(
            new InvoiceDocument("FV/2026/11/000001", OrderId, Guid.NewGuid(), PaymentId, new Money(39.98m, "PLN"), DateTimeOffset.UtcNow));

        var text = Encoding.ASCII.GetString(pdf);
        text.ShouldStartWith("%PDF-1.4");
        text.ShouldEndWith("%%EOF\n");

        // Every xref entry must point exactly at the start of "N 0 obj"; otherwise a PDF reader reports a corrupted file.
        var xref = text[(text.IndexOf("xref\n", StringComparison.Ordinal) + 5)..];
        var entries = xref.Split('\n').Skip(2).Take(5).ToArray();
        for (var index = 0; index < entries.Length; index++)
        {
            var offset = int.Parse(entries[index][..10], System.Globalization.CultureInfo.InvariantCulture);
            text[offset..].ShouldStartWith($"{index + 1} 0 obj");
        }
    }

    private async Task GivenAuthorizedAndCapturedAsync()
    {
        var amount = new MoneyDto { Amount = 39.98m, Currency = "PLN" };
        await _host.DispatchAsync(new AuthorizePayment { OrderId = OrderId, CustomerId = Guid.NewGuid(), Amount = amount, PaymentMethodToken = "pm", PaymentId = PaymentId });
        await _host.DispatchAsync(new CapturePayment { OrderId = OrderId, CustomerId = Guid.NewGuid(), PaymentId = PaymentId, Amount = amount });
    }

    private static PaymentCaptured Captured() => new()
    {
        OrderId = OrderId,
        CustomerId = Guid.NewGuid(),
        PaymentId = PaymentId,
        Amount = new MoneyDto { Amount = 39.98m, Currency = "PLN" },
        ProviderReference = "psp-auth-1",
        CapturedAt = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero),
    };

    private Task<bool> RegisterAsync(Uri blobUrl, Guid eventId) =>
        _host.AsIncomingAsync<InvoiceRegistrationService, bool>(eventId, "invoice-registration", async service =>
        {
            await service.RegisterAsync(blobUrl, CancellationToken.None);
            return true;
        });
}
