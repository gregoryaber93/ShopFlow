using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Payments.V1;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Handling;
using ShopFlow.Messaging.Inbox;
using ShopFlow.Messaging.Outbox;
using ShopFlow.Payments.Application;
using ShopFlow.Payments.Application.Abstractions;
using ShopFlow.Payments.Application.Commands;
using ShopFlow.Payments.Application.Invoicing;
using ShopFlow.Payments.Domain;
using ShopFlow.Payments.Infrastructure;
using ShopFlow.Payments.Infrastructure.Cosmos;
using ShopFlow.SharedKernel.Domain;
using ShopFlow.SharedKernel.Persistence;

namespace ShopFlow.Payments.UnitTests;

/// <summary>Payments composed as in the function app, with an in-memory payment provider and stores.</summary>
internal sealed class PaymentsTestHost : IDisposable
{
    private readonly ServiceProvider _provider;

    public PaymentsTestHost()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton(Store);
        services.AddSingleton(Gateway);
        services.AddSingleton(Invoices);

        services.AddPaymentsMessaging()
            .AddHandler<AuthorizePayment, AuthorizePaymentHandler>()
            .AddHandler<CapturePayment, CapturePaymentHandler>()
            .AddHandler<VoidPayment, VoidPaymentHandler>()
            .AddHandler<RefundPayment, RefundPaymentHandler>()
            .AddHandler<PaymentCaptured, InvoiceGenerationHandler>();
        services.AddPaymentsApplication();

        services.AddScoped<IPaymentRepository, InMemoryPaymentRepository>();
        services.AddSingleton<IPaymentGateway>(provider => provider.GetRequiredService<FakePaymentGateway>());
        services.AddSingleton<IPaymentFeatures, NoFeatures>();
        services.AddSingleton<IInvoiceNumberAllocator, SequentialInvoiceNumbers>();
        services.AddSingleton<IInvoiceRenderer>(new FakeRenderer());
        services.AddSingleton<IInvoiceStorage>(provider => provider.GetRequiredService<FakeInvoiceStorage>());

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero));

    public PaymentStore Store { get; } = new();

    public FakePaymentGateway Gateway { get; } = new();

    public FakeInvoiceStorage Invoices { get; } = new();

    public IServiceProvider Services => _provider;

    public Task<DispatchOutcome> DispatchAsync(IMessage message, Guid? messageId = null)
    {
        var envelope = _provider.GetRequiredService<MessageEnvelopeFactory>().Create(message, new MessageMetadata("corr"), messageId);
        return _provider.GetRequiredService<MessageDispatcher>().DispatchAsync(
            envelope,
            new ConsumerEndpoint(MessagingTopology.Consumers.Payments),
            new DeliveryInfo(1, Time.GetUtcNow()),
            CancellationToken.None);
    }

    /// <summary>Operation in a scope with an "incoming message" set (like a webhook and an Event Grid event).</summary>
    public async Task<TResult> AsIncomingAsync<TService, TResult>(Guid messageId, string consumer, Func<TService, Task<TResult>> action)
        where TService : notnull
    {
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<MessageContextAccessor>().SetIncoming(new MessageContext
        {
            MessageId = messageId,
            MessageType = "external",
            ContractVersion = 1,
            CorrelationId = "corr",
            Source = "test",
            OccurredAt = Time.GetUtcNow(),
            Consumer = consumer,
            DeliveryCount = 1,
            EnqueuedAt = Time.GetUtcNow(),
        });

        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public IReadOnlyList<T> Published<T>()
    {
        var name = MessageTypeRegistry.Default.GetContractId(typeof(T)).Name;
        return [.. Store.Published.Where(message => message.Envelope.MessageType == name).Select(message => JsonSerializer.Deserialize<T>(message.Envelope.Payload.Span, ContractJson.Options)!)];
    }

    public void Dispose() => _provider.Dispose();

    private sealed class NoFeatures : IPaymentFeatures
    {
        public Task<bool> ForceThreeDSecureAsync() => Task.FromResult(false);
    }

    private sealed class FakeRenderer : IInvoiceRenderer
    {
        public byte[] Render(InvoiceDocument invoice) => "%PDF-fake"u8.ToArray();
    }
}

/// <summary>Payments "Cosmos DB" state: documents (through the real mapper), outbox and inbox.</summary>
internal sealed class PaymentStore
{
    private int _version;

    public ConcurrentDictionary<(Guid OrderId, Guid PaymentId), (PaymentDocument Document, string ETag)> Payments { get; } = new();

    public ConcurrentQueue<OutboundMessage> Published { get; } = new();

    public HashSet<InboxEntry> Inbox { get; } = [];

    public string NextETag() => $"\"{Interlocked.Increment(ref _version)}\"";

    public IReadOnlyList<string> PublishedTypes() => [.. Published.Select(message => message.Envelope.MessageType)];
}

internal sealed class InMemoryPaymentRepository(PaymentStore store, IPendingOutbox outbox, IMessageContextAccessor context) : IPaymentRepository
{
    public Task<Payment?> GetAsync(Guid orderId, Guid paymentId, CancellationToken cancellationToken)
    {
        if (!store.Payments.TryGetValue((orderId, paymentId), out var entry))
        {
            return Task.FromResult<Payment?>(null);
        }

        // Rehydration through the real document mapper (the test also covers the mapping), with an ETag as from Cosmos DB.
        var payment = entry.Document.ToDomain();
        payment.SetConcurrencyToken(entry.ETag);
        return Task.FromResult<Payment?>(payment);
    }

    public Task SaveAsync(Payment payment, CancellationToken cancellationToken)
    {
        var key = (payment.OrderId, payment.Id);
        var exists = store.Payments.TryGetValue(key, out var current);

        if ((payment.ConcurrencyToken is null && exists) || (payment.ConcurrencyToken is not null && (!exists || current.ETag != payment.ConcurrencyToken)))
        {
            throw new ConcurrencyConflictException(nameof(Payment), payment.Id.ToString());
        }

        if (context.Current is { } incoming && !store.Inbox.Add(incoming.InboxEntry))
        {
            throw new DuplicateMessageException(incoming.InboxEntry);
        }

        var etag = store.NextETag();
        store.Payments[key] = (PaymentDocument.From(payment), etag);
        foreach (var message in outbox.Drain())
        {
            store.Published.Enqueue(message);
        }

        payment.SetConcurrencyToken(etag);
        return Task.CompletedTask;
    }
}

internal sealed class FakePaymentGateway : IPaymentGateway
{
    public Queue<AuthorizationResult> Authorizations { get; } = new();

    public GatewayOperationResult NextOperation { get; set; } = GatewayOperationResult.Success("op-1");

    public List<string> Calls { get; } = [];

    public Task<AuthorizationResult> AuthorizeAsync(AuthorizationRequest request, CancellationToken cancellationToken)
    {
        Calls.Add("authorize");
        return Task.FromResult(Authorizations.TryDequeue(out var result)
            ? result
            : new AuthorizationResult(AuthorizationOutcome.Authorized, "psp-auth-1", null, null, new DateTimeOffset(2026, 11, 28, 12, 0, 0, TimeSpan.Zero)));
    }

    public Task<GatewayOperationResult> CaptureAsync(Guid paymentId, string providerReference, Money amount, CancellationToken cancellationToken) => Operation("capture");

    public Task<GatewayOperationResult> VoidAsync(Guid paymentId, string providerReference, CancellationToken cancellationToken) => Operation("void");

    public Task<GatewayOperationResult> RefundAsync(Guid paymentId, string providerReference, Money amount, CancellationToken cancellationToken) => Operation("refund");

    private Task<GatewayOperationResult> Operation(string name)
    {
        Calls.Add(name);
        return Task.FromResult(NextOperation);
    }
}

internal sealed class SequentialInvoiceNumbers : IInvoiceNumberAllocator
{
    private readonly ConcurrentDictionary<Guid, string> _allocated = new();
    private int _next;

    public Task<string> AllocateAsync(Guid orderId, DateTimeOffset issuedAt, CancellationToken cancellationToken) =>
        Task.FromResult(_allocated.GetOrAdd(orderId, _ => $"FV/{issuedAt:yyyy}/{issuedAt:MM}/{Interlocked.Increment(ref _next):D6}"));
}

internal sealed class FakeInvoiceStorage : IInvoiceStorage
{
    public Dictionary<string, StoredInvoice> Blobs { get; } = [];

    public Task<string> UploadAsync(InvoiceDocument invoice, byte[] pdf, CancellationToken cancellationToken)
    {
        var path = $"{invoice.IssuedAt:yyyy}/{invoice.IssuedAt:MM}/{invoice.OrderId:D}/{invoice.Number.Replace('/', '-')}.pdf";
        Blobs.TryAdd(path, new StoredInvoice(path, invoice.OrderId, invoice.PaymentId, invoice.Number));
        return Task.FromResult(path);
    }

    public Task<StoredInvoice?> ReadAsync(Uri blobUrl, CancellationToken cancellationToken)
    {
        var path = blobUrl.AbsolutePath.Replace("/invoices/", string.Empty, StringComparison.Ordinal);
        return Task.FromResult(Blobs.TryGetValue(path, out var invoice) ? invoice : null);
    }
}
