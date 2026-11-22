using ShopFlow.Contracts.Common;
using ShopFlow.Messaging;
using ShopFlow.Notifications.Application;
using InventoryV1 = ShopFlow.Contracts.Inventory.V1;
using OrdersV1 = ShopFlow.Contracts.Orders.V1;
using PaymentsV1 = ShopFlow.Contracts.Payments.V1;
using ShippingV1 = ShopFlow.Contracts.Shipping.V1;

namespace ShopFlow.Notifications.UnitTests;

public sealed class NotificationHandlerTests : IDisposable
{
    private static readonly Guid CustomerId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000c1");
    private static readonly Guid OrderId = Guid.Parse("0193a1b2-0000-7000-8000-0000000000c2");

    private readonly NotificationTestHost _host = new();

    public NotificationHandlerTests() =>
        _host.Directory.Contacts[CustomerId] = new CustomerContact(CustomerId, "jan.kowalski@example.com", "Jan", "+48600100200", "pl-PL");

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Confirmed_order_sends_one_polish_email_and_records_a_masked_recipient()
    {
        await _host.DispatchAsync(Confirmed());

        var email = _host.Email.Sent.ShouldHaveSingleItem();
        email.To.ShouldBe("jan.kowalski@example.com");
        email.IsRetry.ShouldBeFalse();
        email.Content.Subject.ShouldBe("Zamówienie SF-2026-000123 zostało potwierdzone");
        email.Content.Html.ShouldContain($"https://shopflow.example/account/orders/{OrderId:D}");
        email.Content.PlainText.ShouldContain("Cześć Jan,");

        var entry = _host.Log.Entries.Values.ShouldHaveSingleItem();
        entry.Status.ShouldBe(NotificationStatus.Sent);
        entry.Completion!.MaskedRecipient.ShouldBe("j***@example.com");
        entry.Draft.PartitionKey.ShouldBe(CustomerId.ToString("D"));
    }

    [Fact]
    public async Task Redelivered_message_does_not_send_a_second_email()
    {
        var envelope = _host.Envelope(Confirmed());

        await _host.DispatchAsync(envelope);
        await _host.DispatchAsync(envelope);

        _host.Email.Sent.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Attempt_that_died_after_claiming_is_resumed_as_a_retry()
    {
        var messageId = Guid.CreateVersion7();
        _host.Log.SeedPending(NotificationDraft.ForCustomer(Context(messageId), TemplateNames.OrderConfirmed, NotificationChannel.Email, CustomerId, OrderId));

        await _host.DispatchAsync(_host.Envelope(Confirmed(), messageId));

        // The adapter gets a signal that the previous attempt may have reached the provider, and checks that itself.
        _host.Email.Sent.ShouldHaveSingleItem().IsRetry.ShouldBeTrue();
        _host.Log.Entries.Values.ShouldHaveSingleItem().Attempt.ShouldBe(2);
    }

    [Fact]
    public async Task Deleted_customer_account_is_skipped_with_a_reason_instead_of_failing()
    {
        _host.Directory.Contacts.Clear();

        await _host.DispatchAsync(Confirmed());

        _host.Email.Sent.ShouldBeEmpty();
        var entry = _host.Log.Entries.Values.ShouldHaveSingleItem();
        entry.Status.ShouldBe(NotificationStatus.Skipped);
        entry.Completion!.Reason.ShouldBe("customer_not_found");
    }

    [Fact]
    public async Task Provider_rejection_is_recorded_and_the_message_is_acknowledged()
    {
        _host.Email.Next = DeliveryResult.Rejected("EmailDroppedAllRecipientsSuppressed");

        var outcome = await _host.DispatchAsync(Confirmed());

        outcome.ShouldBe(Messaging.Handling.DispatchOutcome.Handled);
        _host.Log.Entries.Values.ShouldHaveSingleItem().Status.ShouldBe(NotificationStatus.Rejected);
    }

    [Fact]
    public async Task Zero_refund_only_released_a_hold_and_sends_nothing()
    {
        await _host.DispatchAsync(new PaymentsV1.PaymentRefunded
        {
            OrderId = OrderId,
            CustomerId = CustomerId,
            PaymentId = Guid.NewGuid(),
            Amount = new MoneyDto { Amount = 0m, Currency = "PLN" },
            RefundedAt = DateTimeOffset.UtcNow,
        });

        _host.Email.Sent.ShouldBeEmpty();
        _host.Log.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Invoice_email_carries_a_fresh_link_and_says_how_long_it_works()
    {
        await _host.DispatchAsync(new PaymentsV1.InvoiceIssued
        {
            OrderId = OrderId,
            CustomerId = CustomerId,
            InvoiceNumber = "FV-2026-11-000123",
            Total = new MoneyDto { Amount = 249.99m, Currency = "PLN" },
            BlobPath = $"2026/11/{OrderId:D}/FV-2026-11-000123.pdf",
            IssuedAt = DateTimeOffset.UtcNow,
        });

        var email = _host.Email.Sent.ShouldHaveSingleItem();
        email.Content.Html.ShouldContain("https://files.shopflow.example/invoices/2026/11/");
        email.Content.PlainText.ShouldContain("72 godz.");
        email.Content.PlainText.ShouldContain("249,99 PLN");
    }

    [Fact]
    public async Task Shipment_sms_goes_out_only_for_customers_in_the_rollout_and_without_diacritics()
    {
        await _host.DispatchAsync(Dispatched());
        _host.Sms.Sent.ShouldBeEmpty();

        _host.Features.ShipmentSms = true;
        await _host.DispatchAsync(Dispatched());

        var sms = _host.Sms.Sent.ShouldHaveSingleItem();
        sms.To.ShouldBe("+48600100200");
        sms.Content.Text.ShouldContain("https://tracking.carrier.example/parcels/TRK-777");
        sms.Content.Text.ShouldAllBe(character => character < 128);
        _host.Email.Sent.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Low_stock_alert_is_sent_once_per_sku_warehouse_and_day()
    {
        var morning = new DateTimeOffset(2026, 11, 21, 8, 0, 0, TimeSpan.Zero);

        await _host.DispatchAsync(LowStock(morning));
        await _host.DispatchAsync(LowStock(morning.AddHours(6)));
        await _host.DispatchAsync(LowStock(morning.AddDays(1)));

        _host.Email.Sent.Count.ShouldBe(2);
        _host.Email.Sent.ShouldAllBe(email => email.To == "ops@shopflow.example");
        _host.Log.Entries.Values.ShouldAllBe(entry => entry.Draft.PartitionKey == NotificationDraft.OperationsPartition);
    }

    [Fact]
    public async Task Unknown_rejection_reason_gets_a_generic_text_in_the_customers_language()
    {
        _host.Directory.Contacts[CustomerId] = _host.Directory.Contacts[CustomerId] with { PreferredLanguage = "en-US" };

        await _host.DispatchAsync(new OrdersV1.OrderRejected
        {
            OrderId = OrderId,
            CustomerId = CustomerId,
            OrderNumber = "SF-2026-000123",
            ReasonCode = "fraud_suspected",
            ReasonDescription = "Risk score 97 from PSP",
            RejectedAt = DateTimeOffset.UtcNow,
        });

        var email = _host.Email.Sent.ShouldHaveSingleItem();
        email.Content.Subject.ShouldBe("We could not complete order SF-2026-000123");
        email.Content.PlainText.ShouldContain("Something went wrong on our side.");
        email.Content.PlainText.ShouldNotContain("Risk score");
    }

    [Fact]
    public async Task Customer_controlled_name_is_html_encoded()
    {
        _host.Directory.Contacts[CustomerId] = _host.Directory.Contacts[CustomerId] with
        {
            DisplayName = "<a href=\"https://evil.example\">Kliknij</a>",
        };

        await _host.DispatchAsync(Confirmed());

        var html = _host.Email.Sent.ShouldHaveSingleItem().Content.Html;
        html.ShouldNotContain("https://evil.example\">");
        html.ShouldContain("&lt;a href=&quot;https://evil.example&quot;&gt;");
    }

    private static OrdersV1.OrderConfirmed Confirmed() => new()
    {
        OrderId = OrderId,
        CustomerId = CustomerId,
        OrderNumber = "SF-2026-000123",
        Total = new MoneyDto { Amount = 249.99m, Currency = "PLN" },
        ConfirmedAt = DateTimeOffset.UtcNow,
    };

    private static ShippingV1.ShipmentDispatched Dispatched() => new()
    {
        OrderId = OrderId,
        CustomerId = CustomerId,
        ShipmentId = Guid.NewGuid(),
        CarrierCode = "DPD",
        TrackingNumber = "TRK-777",
        DispatchedAt = DateTimeOffset.UtcNow,
        EstimatedDeliveryDate = new DateOnly(2026, 11, 24),
    };

    private static InventoryV1.StockLevelLow LowStock(DateTimeOffset detectedAt) => new()
    {
        Sku = "SKU-1",
        WarehouseCode = "WAW-1",
        Available = 3,
        ReorderThreshold = 5,
        DetectedAt = detectedAt,
    };

    private static MessageContext Context(Guid messageId) => new()
    {
        MessageId = messageId,
        MessageType = "OrderConfirmed",
        ContractVersion = 1,
        CorrelationId = "corr",
        Source = "orders",
        OccurredAt = DateTimeOffset.UtcNow,
        Consumer = "notifications-processor",
        DeliveryCount = 1,
        EnqueuedAt = DateTimeOffset.UtcNow,
    };
}
