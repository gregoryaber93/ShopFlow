using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;
using ShopFlow.Payments.Application.Abstractions;

namespace ShopFlow.Payments.Infrastructure.Invoicing;

/// <summary>
/// Minimal PDF 1.4 with one page of text, generated without external libraries.
/// </summary>
/// <remarks>
/// WHY: popular PDF libraries for .NET have commercial licences or require registration for companies. In this
/// repository the invoice is a pretext for showing the Blob Storage → Event Grid → Service Bus flow, not document
/// composition. In production a document template service would sit here. The standard Helvetica font has no glyphs
/// for characters outside WinAnsi (e.g. Polish letters in customer names), so text is transliterated.
/// </remarks>
internal sealed class SimplePdfInvoiceRenderer : IInvoiceRenderer
{
    public byte[] Render(InvoiceDocument invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        // WHY: a VAT invoice issued by a Polish company is a customer-facing legal document, so its text stays Polish
        // (like the customer e-mail templates). Code, comments and logs around it are English.
        string[] lines =
        [
            $"FAKTURA VAT {invoice.Number}",
            $"Data wystawienia: {invoice.IssuedAt:yyyy-MM-dd}",
            "Sprzedawca: ShopFlow sp. z o.o., ul. Przykladowa 1, 00-001 Warszawa, NIP 000-000-00-00",
            $"Nabywca: klient {invoice.CustomerId:D}",
            $"Zamowienie: {invoice.OrderId:D}",
            $"Platnosc: {invoice.PaymentId:D}",
            string.Create(CultureInfo.InvariantCulture, $"Do zaplaty (brutto): {invoice.Amount.Amount:0.00} {invoice.Amount.Currency} - zaplacono"),
        ];

        var text = new StringBuilder("BT\n/F1 18 Tf\n50 780 Td\n");
        text.Append('(').Append(Escape(lines[0])).Append(") Tj\n/F1 11 Tf\n");
        foreach (var line in lines.Skip(1))
        {
            text.Append("0 -24 Td\n(").Append(Escape(line)).Append(") Tj\n");
        }

        text.Append("ET\n");
        var stream = text.ToString();

        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            string.Create(CultureInfo.InvariantCulture, $"<< /Length {Encoding.ASCII.GetByteCount(stream)} >>\nstream\n{stream}endstream"),
        ];

        // The xref table points at the byte offset of every object, so offsets are counted in ASCII bytes.
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            pdf.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            var ascii = character switch
            {
                'ą' => 'a',
                'ć' => 'c',
                'ę' => 'e',
                'ł' => 'l',
                'ń' => 'n',
                'ó' => 'o',
                'ś' => 's',
                'ź' or 'ż' => 'z',
                'Ą' => 'A',
                'Ć' => 'C',
                'Ę' => 'E',
                'Ł' => 'L',
                'Ń' => 'N',
                'Ó' => 'O',
                'Ś' => 'S',
                'Ź' or 'Ż' => 'Z',
                < (char)32 or > (char)126 => '?',
                _ => character,
            };

            if (ascii is '(' or ')' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(ascii);
        }

        return builder.ToString();
    }
}

public sealed class InvoiceStorageOptions
{
    public const string SectionName = "InvoiceStorage";

    /// <summary>E.g. <c>https://stshopflowdocsprodweu.blob.core.windows.net</c>. Microsoft.Extensions.Azure also reads the <c>serviceUri</c> key.</summary>
    [Required]
    public Uri? ServiceUri { get; set; }

    public string ContainerName { get; set; } = "invoices";
}

/// <summary>
/// Invoices in the <c>invoices</c> container: <c>{yyyy}/{MM}/{orderId}/FV-2026-11-000123.pdf</c>.
/// </summary>
/// <remarks>
/// AZURE: the Storage account has shared key access and anonymous access disabled, and the public endpoint is closed
/// (a private endpoint for blob). The function's identity has the Storage Blob Data Contributor role only on the
/// invoices container. A lifecycle rule moves invoices to the cool tier after 30 days, cold after 180, archive after a
/// year, and deletes them after 6 years (retention requirement for accounting documents). Versioning and soft delete
/// protect against accidental overwrites.
/// </remarks>
internal sealed class BlobInvoiceStorage(BlobServiceClient blobs, IOptions<InvoiceStorageOptions> options) : IInvoiceStorage
{
    private const string OrderIdKey = "orderid";
    private const string PaymentIdKey = "paymentid";
    private const string InvoiceNumberKey = "invoicenumber";

    private BlobContainerClient Container => blobs.GetBlobContainerClient(options.Value.ContainerName);

    public async Task<string> UploadAsync(InvoiceDocument invoice, byte[] pdf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoice);

        var path = string.Create(CultureInfo.InvariantCulture, $"{invoice.IssuedAt:yyyy}/{invoice.IssuedAt:MM}/{invoice.OrderId:D}/{invoice.Number.Replace('/', '-')}.pdf");
        var blob = Container.GetBlobClient(path);

        try
        {
            await blob.UploadAsync(BinaryData.FromBytes(pdf), new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/pdf" },

                // Invoice registration reads the metadata after the BlobCreated event (the event does not contain it).
                Metadata = new Dictionary<string, string>
                {
                    [OrderIdKey] = invoice.OrderId.ToString("D"),
                    [PaymentIdKey] = invoice.PaymentId.ToString("D"),
                    [InvoiceNumberKey] = invoice.Number,
                },

                // If-None-Match: * = "write only if it does not exist". A retry after a failure does not overwrite the invoice
                // and does not trigger a second BlobCreated event.
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (RequestFailedException exception) when (exception.Status == 409)
        {
            // The file is already there (the previous attempt managed to write it).
        }

        return path;
    }

    public async Task<StoredInvoice?> ReadAsync(Uri blobUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(blobUrl);

        var parts = new BlobUriBuilder(blobUrl);
        if (!string.Equals(parts.BlobContainerName, options.Value.ContainerName, StringComparison.Ordinal))
        {
            return null;
        }

        var properties = await Container.GetBlobClient(parts.BlobName).GetPropertiesAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var metadata = new Dictionary<string, string>(properties.Value.Metadata, StringComparer.OrdinalIgnoreCase);

        return metadata.TryGetValue(OrderIdKey, out var orderId) && Guid.TryParse(orderId, out var order)
            && metadata.TryGetValue(PaymentIdKey, out var paymentId) && Guid.TryParse(paymentId, out var payment)
            && metadata.TryGetValue(InvoiceNumberKey, out var number)
            ? new StoredInvoice(parts.BlobName, order, payment, number)
            : null;
    }
}
