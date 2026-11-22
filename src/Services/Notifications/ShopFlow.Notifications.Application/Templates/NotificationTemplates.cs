using System.Globalization;
using Microsoft.Extensions.Options;
using ShopFlow.Contracts.Common;
using ShopFlow.Contracts.Orders.V1;
using ShopFlow.Notifications.Application.Templates;
using InventoryV1 = ShopFlow.Contracts.Inventory.V1;
using PaymentsV1 = ShopFlow.Contracts.Payments.V1;
using ShippingV1 = ShopFlow.Contracts.Shipping.V1;

namespace ShopFlow.Notifications.Application;

/// <summary>
/// Notification content in two languages.
/// </summary>
/// <remarks>
/// WHY: templates live in code, not in an external template system. They are versioned together with the contracts they
/// take data from and covered by tests. When the marketing team starts changing the content, they move to a template
/// store (e.g. Blob Storage with versions), but the template's data contract stays the same.
/// The Polish strings are customer-facing localization (pl-PL customers), not documentation.
/// </remarks>
public sealed class NotificationTemplates(IOptions<NotificationOptions> options)
{
    public EmailContent OrderConfirmed(OrderConfirmed message, Recipient recipient)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(recipient);
        var language = recipient.Language;

        return new EmailComposer(language, Localize(language, $"Zamówienie {message.OrderNumber} zostało potwierdzone", $"Your order {message.OrderNumber} is confirmed"))
            .Greeting(recipient.DisplayName)
            .Paragraph(Localize(
                language,
                "Płatność została przyjęta, a zamówienie przekazaliśmy do magazynu. Napiszemy ponownie, gdy paczka wyruszy w drogę.",
                "We have received your payment and passed your order to our warehouse. We will write again when your parcel is on its way."))
            .Fact(Localize(language, "Numer zamówienia", "Order number"), message.OrderNumber)
            .Fact(Localize(language, "Kwota", "Total"), FormatMoney(message.Total, language))
            .Action(Localize(language, "Zobacz zamówienie", "View order"), OrderUrl(message.OrderId))
            .Build();
    }

    public EmailContent OrderRejected(OrderRejected message, Recipient recipient)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(recipient);
        var language = recipient.Language;

        // WHY: ReasonDescription never reaches the customer. It is a technical description from the saga (e.g. a bank decline
        // code), useful in logs but not in an e-mail.
        return new EmailComposer(language, Localize(language, $"Nie udało się zrealizować zamówienia {message.OrderNumber}", $"We could not complete order {message.OrderNumber}"))
            .Greeting(recipient.DisplayName)
            .Paragraph(RejectionReason(message.ReasonCode, language))
            .Paragraph(Localize(
                language,
                "Jeżeli bank zablokował środki na Twojej karcie, zwolni je w ciągu kilku dni roboczych. Nie pobraliśmy żadnej opłaty.",
                "If your bank put a hold on your card, it will release it within a few business days. You have not been charged."))
            .Action(Localize(language, "Wróć do sklepu", "Back to the shop"), options.Value.ShopBaseUrl!)
            .Build();
    }

    public EmailContent OrderCancelled(OrderCancelled message, Recipient recipient)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(recipient);
        var language = recipient.Language;

        return new EmailComposer(language, Localize(language, $"Zamówienie {message.OrderNumber} zostało anulowane", $"Order {message.OrderNumber} has been cancelled"))
            .Greeting(recipient.DisplayName)
            .Paragraph(Localize(
                language,
                "Anulowaliśmy Twoje zamówienie. Jeżeli płatność została już pobrana, zwrot potwierdzimy osobną wiadomością.",
                "We have cancelled your order. If you have already been charged, we will confirm the refund in a separate message."))
            .Action(Localize(language, "Zobacz zamówienie", "View order"), OrderUrl(message.OrderId))
            .Build();
    }

    public EmailContent RefundIssued(PaymentsV1.PaymentRefunded message, Recipient recipient)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(recipient);
        var language = recipient.Language;
        var amount = FormatMoney(message.Amount, language);

        return new EmailComposer(language, Localize(language, $"Zwróciliśmy {amount}", $"We have refunded {amount}"))
            .Greeting(recipient.DisplayName)
            .Paragraph(Localize(
                language,
                "Zleciliśmy zwrot na kartę użytą przy zamówieniu. Środki pojawią się na koncie zwykle w ciągu 3–5 dni roboczych.",
                "We have refunded the card used for your order. The money usually appears in your account within 3–5 business days."))
            .Fact(Localize(language, "Kwota zwrotu", "Refund amount"), amount)
            .Action(Localize(language, "Zobacz zamówienie", "View order"), OrderUrl(message.OrderId))
            .Build();
    }

    /// <remarks>
    /// WHY: a link instead of an attachment. An attachment stays in the mailbox (and its backups) forever, while a link expires
    /// and can be revoked. The invoice also has a single source of truth: a correction replaces the file instead of sending
    /// out a second version.
    /// </remarks>
    public EmailContent InvoiceIssued(PaymentsV1.InvoiceIssued message, Uri downloadLink, TimeSpan linkLifetime, Recipient recipient)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(recipient);
        var language = recipient.Language;
        var hours = (int)linkLifetime.TotalHours;

        return new EmailComposer(language, Localize(language, $"Faktura {message.InvoiceNumber}", $"Invoice {message.InvoiceNumber}"))
            .Greeting(recipient.DisplayName)
            .Paragraph(Localize(language, "Faktura za Twoje zamówienie jest gotowa do pobrania.", "The invoice for your order is ready to download."))
            .Fact(Localize(language, "Numer faktury", "Invoice number"), message.InvoiceNumber)
            .Fact(Localize(language, "Kwota", "Total"), FormatMoney(message.Total, language))
            .Action(Localize(language, "Pobierz fakturę (PDF)", "Download invoice (PDF)"), downloadLink)
            .Paragraph(Localize(
                language,
                $"Link jest ważny przez {hours} godz. Później fakturę pobierzesz w szczegółach zamówienia po zalogowaniu.",
                $"The link is valid for {hours} hours. After that, you can download the invoice from your order details after signing in."))
            .Action(Localize(language, "Szczegóły zamówienia", "Order details"), OrderUrl(message.OrderId))
            .Build();
    }

    public EmailContent ShipmentDispatched(ShippingV1.ShipmentDispatched message, Recipient recipient)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(recipient);
        var language = recipient.Language;

        var composer = new EmailComposer(language, Localize(language, "Twoja paczka jest w drodze", "Your parcel is on its way"))
            .Greeting(recipient.DisplayName)
            .Paragraph(Localize(language, "Przewoźnik odebrał paczkę z naszego magazynu.", "The carrier has collected your parcel from our warehouse."))
            .Fact(Localize(language, "Przewoźnik", "Carrier"), message.CarrierCode)
            .Fact(Localize(language, "Numer przesyłki", "Tracking number"), message.TrackingNumber);

        if (message.EstimatedDeliveryDate is { } eta)
        {
            composer.Fact(Localize(language, "Przewidywana dostawa", "Estimated delivery"), eta.ToString("d MMMM yyyy", Culture(language)));
        }

        return composer
            .Action(Localize(language, "Śledź przesyłkę", "Track parcel"), TrackingUrl(message.CarrierCode, message.TrackingNumber) ?? OrderUrl(message.OrderId))
            .Build();
    }

    /// <remarks>
    /// WHY: the Polish SMS has no Polish diacritics. A single character outside the GSM-7 alphabet (e.g. "ś") switches the
    /// whole message to UCS-2, and the segment limit drops from 160 to 70 characters. Every segment is billed separately, so a
    /// diacritic in "Sledz" can double the cost of the send.
    /// </remarks>
    public SmsContent ShipmentDispatchedSms(ShippingV1.ShipmentDispatched message, Recipient recipient)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(recipient);

        var link = TrackingUrl(message.CarrierCode, message.TrackingNumber) ?? OrderUrl(message.OrderId);
        return new SmsContent(Localize(
            recipient.Language,
            $"ShopFlow: paczka {message.TrackingNumber} jest w drodze. Sledz: {link.AbsoluteUri}",
            $"ShopFlow: parcel {message.TrackingNumber} is on its way. Track: {link.AbsoluteUri}"));
    }

    public EmailContent ShipmentDelivered(ShippingV1.ShipmentDelivered message, Recipient recipient)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(recipient);
        var language = recipient.Language;

        return new EmailComposer(language, Localize(language, "Paczka została doręczona", "Your parcel has been delivered"))
            .Greeting(recipient.DisplayName)
            .Paragraph(Localize(
                language,
                "Przewoźnik potwierdził doręczenie. Jeżeli paczka do Ciebie nie dotarła, odpowiedz na tę wiadomość.",
                "The carrier has confirmed delivery. If you have not received your parcel, reply to this message."))
            .Action(Localize(language, "Zobacz zamówienie", "View order"), OrderUrl(message.OrderId))
            .Build();
    }

    public EmailContent StockLevelLow(InventoryV1.StockLevelLow message)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Internal alert for the operations team, so it is always in English (the team's working language).
        return new EmailComposer("en", $"[ShopFlow] Low stock: {message.Sku} in {message.WarehouseCode}")
            .Paragraph("Available stock of the product dropped below the reorder threshold.")
            .Fact("SKU", message.Sku)
            .Fact("Warehouse", message.WarehouseCode)
            .Fact("Available", message.Available.ToString(CultureInfo.InvariantCulture))
            .Fact("Reorder threshold", message.ReorderThreshold.ToString(CultureInfo.InvariantCulture))
            .Paragraph("The next alert for this product in this warehouse will come tomorrow at the earliest.")
            .Build();
    }

    /// <remarks>
    /// WHY: an unknown reason code gets a generic text instead of an exception. The contract deliberately uses strings, not an
    /// enum, so that Orders can add a new reason without breaking consumers. An exception would mean the DLQ and no e-mail.
    /// </remarks>
    private static string RejectionReason(string reasonCode, string language) => reasonCode switch
    {
        OrderRejectionReasons.OutOfStock => Localize(language, "Część produktów przestała być dostępna.", "Some items are no longer in stock."),
        OrderRejectionReasons.PaymentDeclined => Localize(language, "Bank odrzucił płatność.", "Your bank declined the payment."),
        OrderRejectionReasons.PaymentTimeout => Localize(language, "Nie otrzymaliśmy potwierdzenia płatności w wyznaczonym czasie.", "We did not receive payment confirmation in time."),
        OrderRejectionReasons.ShipmentUnavailable => Localize(language, "Nie możemy dostarczyć przesyłki pod wskazany adres.", "We cannot deliver to the selected address."),
        OrderRejectionReasons.CancelledByCustomer => Localize(language, "Zamówienie zostało anulowane na Twoją prośbę.", "The order was cancelled at your request."),
        _ => Localize(language, "Wystąpił problem po naszej stronie. Przepraszamy.", "Something went wrong on our side. We are sorry."),
    };

    private static string Localize(string language, string polish, string english) => language == "en" ? english : polish;

    // AZURE: culture-aware formatting needs ICU data. ShopFlow function apps usually have InvariantGlobalization=true
    // (smaller image, faster start), but here that would be a bug: in invariant mode GetCultureInfo("pl-PL") throws.
    // That is why the Notifications.Functions project turns it off.
    private static CultureInfo Culture(string language) => CultureInfo.GetCultureInfo(language == "en" ? "en-GB" : "pl-PL");

    // WHY: the currency code instead of a symbol. The shop accepts several currencies, and "zł" or "€" in the wrong culture
    // can be misleading.
    private static string FormatMoney(MoneyDto money, string language) =>
        string.Create(Culture(language), $"{money.Amount:N2} {money.Currency}");

    private Uri OrderUrl(Guid orderId) => new(options.Value.ShopBaseUrl!, $"account/orders/{orderId:D}");

    private Uri? TrackingUrl(string carrierCode, string trackingNumber) =>
        options.Value.CarrierTrackingUrls.TryGetValue(carrierCode, out var template)
            ? new Uri(template.Replace("{trackingNumber}", Uri.EscapeDataString(trackingNumber), StringComparison.Ordinal))
            : null;
}
