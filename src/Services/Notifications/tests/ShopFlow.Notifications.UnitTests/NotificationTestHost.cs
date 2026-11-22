using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ShopFlow.Contracts;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Handling;
using ShopFlow.Notifications.Application;
using ShopFlow.Notifications.Infrastructure;
using InventoryV1 = ShopFlow.Contracts.Inventory.V1;
using OrdersV1 = ShopFlow.Contracts.Orders.V1;
using PaymentsV1 = ShopFlow.Contracts.Payments.V1;
using ShippingV1 = ShopFlow.Contracts.Shipping.V1;

namespace ShopFlow.Notifications.UnitTests;

/// <summary>
/// Real handlers, dispatcher and NotificationSender; only the ports to the outside world are replaced.
/// </summary>
internal sealed class NotificationTestHost : IDisposable
{
    private readonly ServiceProvider _provider;

    public NotificationTestHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new NotificationOptions
        {
            ShopBaseUrl = new Uri("https://shopflow.example/"),
            OperationsEmail = "ops@shopflow.example",
            CarrierTrackingUrls = new(StringComparer.OrdinalIgnoreCase) { ["DPD"] = "https://tracking.carrier.example/parcels/{trackingNumber}" },
        }));

        services.AddSingleton<INotificationLog>(Log);
        services.AddSingleton<ICustomerDirectory>(Directory);
        services.AddSingleton<IEmailSender>(Email);
        services.AddSingleton<ISmsSender>(Sms);
        services.AddSingleton<IInvoiceLinks>(InvoiceLinks);
        services.AddSingleton<INotificationFeatures>(Features);

        services.AddNotificationsMessaging()
            .AddHandler<OrdersV1.OrderConfirmed, OrderNotificationHandlers>()
            .AddHandler<OrdersV1.OrderRejected, OrderNotificationHandlers>()
            .AddHandler<OrdersV1.OrderCancelled, OrderNotificationHandlers>()
            .AddHandler<PaymentsV1.PaymentRefunded, PaymentNotificationHandlers>()
            .AddHandler<PaymentsV1.InvoiceIssued, PaymentNotificationHandlers>()
            .AddHandler<ShippingV1.ShipmentDispatched, ShipmentNotificationHandlers>()
            .AddHandler<ShippingV1.ShipmentDelivered, ShipmentNotificationHandlers>()
            .AddHandler<InventoryV1.StockLevelLow, StockAlertHandler>();
        services.AddNotificationsApplication();

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public InMemoryNotificationLog Log { get; } = new();

    public FakeCustomerDirectory Directory { get; } = new();

    public FakeEmailSender Email { get; } = new();

    public FakeSmsSender Sms { get; } = new();

    public FakeInvoiceLinks InvoiceLinks { get; } = new();

    public FakeFeatures Features { get; } = new();

    public MessageEnvelope Envelope(IMessage message, Guid? messageId = null) =>
        _provider.GetRequiredService<MessageEnvelopeFactory>().Create(message, new MessageMetadata("corr"), messageId);

    public Task<DispatchOutcome> DispatchAsync(IMessage message) => DispatchAsync(Envelope(message));

    public Task<DispatchOutcome> DispatchAsync(MessageEnvelope envelope) =>
        _provider.GetRequiredService<MessageDispatcher>().DispatchAsync(
            envelope,
            new ConsumerEndpoint(MessagingTopology.Consumers.Notifications),
            new DeliveryInfo(1, DateTimeOffset.UtcNow),
            CancellationToken.None);

    public void Dispose() => _provider.Dispose();
}

internal sealed class InMemoryNotificationLog : INotificationLog
{
    public Dictionary<Guid, Entry> Entries { get; } = [];

    public Task<bool> IsCompletedAsync(NotificationDraft draft, CancellationToken cancellationToken) =>
        Task.FromResult(Entries.TryGetValue(draft.NotificationId, out var entry) && entry.Status != NotificationStatus.Pending);

    public Task<NotificationClaim?> TryClaimAsync(NotificationDraft draft, CancellationToken cancellationToken)
    {
        var attempt = 1;
        if (Entries.TryGetValue(draft.NotificationId, out var existing))
        {
            if (existing.Status != NotificationStatus.Pending)
            {
                return Task.FromResult<NotificationClaim?>(null);
            }

            attempt = existing.Attempt + 1;
        }

        Entries[draft.NotificationId] = new Entry(draft, NotificationStatus.Pending, attempt, null);
        return Task.FromResult<NotificationClaim?>(new NotificationClaim(draft, attempt, DateTimeOffset.UtcNow, $"etag-{attempt}"));
    }

    public Task CompleteAsync(NotificationClaim claim, NotificationCompletion completion, CancellationToken cancellationToken)
    {
        Entries[claim.Draft.NotificationId] = new Entry(claim.Draft, completion.Status, claim.Attempt, completion);
        return Task.CompletedTask;
    }

    /// <summary>Simulates an attempt that reserved the send and crashed before recording the outcome.</summary>
    public void SeedPending(NotificationDraft draft) => Entries[draft.NotificationId] = new Entry(draft, NotificationStatus.Pending, 1, null);

    public sealed record Entry(NotificationDraft Draft, NotificationStatus Status, int Attempt, NotificationCompletion? Completion);
}

internal sealed class FakeCustomerDirectory : ICustomerDirectory
{
    public Dictionary<Guid, CustomerContact> Contacts { get; } = [];

    public Task<CustomerContact?> FindAsync(Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult(Contacts.GetValueOrDefault(customerId));
}

internal sealed class FakeEmailSender : IEmailSender
{
    public List<OutgoingEmail> Sent { get; } = [];

    public DeliveryResult Next { get; set; } = DeliveryResult.Sent("acs-operation");

    public Task<DeliveryResult> SendAsync(OutgoingEmail email, CancellationToken cancellationToken)
    {
        Sent.Add(email);
        return Task.FromResult(Next);
    }
}

internal sealed class FakeSmsSender : ISmsSender
{
    public List<OutgoingSms> Sent { get; } = [];

    public Task<DeliveryResult> SendAsync(OutgoingSms sms, CancellationToken cancellationToken)
    {
        Sent.Add(sms);
        return Task.FromResult(DeliveryResult.Sent("sms-message"));
    }
}

internal sealed class FakeInvoiceLinks : IInvoiceLinks
{
    public TimeSpan Lifetime => TimeSpan.FromHours(72);

    public Task<Uri> CreateAsync(string blobPath, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri($"https://files.shopflow.example/invoices/{blobPath}?sp=r&sig=test"));
}

internal sealed class FakeFeatures : INotificationFeatures
{
    public bool ShipmentSms { get; set; }

    public Task<bool> IsShipmentSmsEnabledAsync(Guid customerId) => Task.FromResult(ShipmentSms);
}
