using Azure.Communication.Email;
using Azure.Communication.Sms;
using Azure.Core;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.FeatureManagement;
using Microsoft.FeatureManagement.FeatureFilters;
using ShopFlow.Azure.Storage;
using ShopFlow.Contracts;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Failures;
using ShopFlow.Notifications.Application;
using ShopFlow.Notifications.Infrastructure.Channels;
using ShopFlow.Notifications.Infrastructure.Cosmos;
using ShopFlow.Notifications.Infrastructure.Customers;
using ShopFlow.Observability;
using ShopFlow.Persistence.Cosmos;
using ShopFlow.Resilience;

namespace ShopFlow.Notifications.Infrastructure;

public sealed class InvoiceLinkOptions
{
    public const string SectionName = "InvoiceLinks";

    /// <summary>Documents account (the same one Payments writes invoices to).</summary>
    public Uri? ServiceUri { get; set; }

    public string ContainerName { get; set; } = "invoices";

    /// <summary>
    /// Lifetime of the link in the e-mail.
    /// </summary>
    /// <remarks>
    /// WHY: a trade-off. A link in an e-mail is a bearer token that stays in the mailbox, so shorter means safer. Customers
    /// sometimes open the e-mail after a weekend, though, hence 72 hours. Later the invoice is downloaded after signing in, and
    /// the page issues a fresh link valid for a few minutes.
    /// </remarks>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromHours(72);
}

internal sealed class BlobInvoiceLinks(BlobReadLinkFactory links, IOptions<InvoiceLinkOptions> options) : IInvoiceLinks
{
    public TimeSpan Lifetime => options.Value.Lifetime;

    public Task<Uri> CreateAsync(string blobPath, CancellationToken cancellationToken) =>
        links.CreateReadLinkAsync(options.Value.ContainerName, blobPath, options.Value.Lifetime, cancellationToken);
}

internal sealed class NotificationFeatures(IFeatureManager featureManager) : INotificationFeatures
{
    /// <remarks>
    /// AZURE: a flag in App Configuration with the <c>Microsoft.Targeting</c> filter (e.g. 10% of customers plus a list of
    /// testers by identifier). We pass the context explicitly, because a function has no HTTP request with a signed-in user the
    /// library could read it from. The filter computes the assignment from a hash of the identifier, so the decision for a
    /// given customer is stable across instances and retries.
    /// </remarks>
    public Task<bool> IsShipmentSmsEnabledAsync(Guid customerId) =>
        featureManager.IsEnabledAsync(NotificationFeatureFlags.ShipmentSms, new TargetingContext { UserId = customerId.ToString("D") });
}

public static class NotificationsInfrastructureExtensions
{
    /// <summary>
    /// Notifications only consumes: it has no publishing routes and no outbox, so the application identity needs only the
    /// Data Receiver role on the notifications-inbox queue.
    /// </summary>
    public static MessagingBuilder AddNotificationsMessaging(this IServiceCollection services) =>
        services.AddShopFlowMessaging(MessagingTopology.Consumers.Notifications);

    public static IServiceCollection AddNotificationsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<NotificationOptions>()
            .Bind(configuration.GetSection(NotificationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<CommunicationOptions>()
            .Bind(configuration.GetSection(CommunicationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<CustomerDirectoryOptions>()
            .Bind(configuration.GetSection(CustomerDirectoryOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<InvoiceLinkOptions>()
            .Bind(configuration.GetSection(InvoiceLinkOptions.SectionName))
            .Validate(options => options.Lifetime <= BlobReadLinkFactory.MaxLinkLifetime, "InvoiceLinks:Lifetime exceeds the user delegation key limit.")
            .ValidateOnStart();
        services.AddOptions<BlobLinkOptions>().Bind(configuration.GetSection(BlobLinkOptions.SectionName));

        services.TryAddSingleton(TimeProvider.System);

        // Send history (Cosmos DB, notifications database).
        services.AddShopFlowCosmos(configuration);
        services.AddScoped<INotificationLog, CosmosNotificationLog>();
        services.AddSingleton<NotificationHistoryReader>();

        // Customer directory: Graph in the External ID tenant.
        services.AddHybridCache();
        services.AddSingleton(provider => new GraphCredential(ExternalTenantCredential.Create(
            provider.GetRequiredService<TokenCredential>(),
            provider.GetRequiredService<IOptions<CustomerDirectoryOptions>>().Value)));

        var graph = services.AddHttpClient<ICustomerDirectory, GraphCustomerDirectory>((provider, client) =>
        {
            client.BaseAddress = provider.GetRequiredService<IOptions<CustomerDirectoryOptions>>().Value.GraphBaseUrl;
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        graph.AddMicrosoftGraphResilience();

        // WHY: the credential is a singleton because it holds a token cache, while the handler is created anew for every chain
        // (the HttpClient factory rotates chains every few minutes, and one DelegatingHandler instance cannot be inserted into two
        // chains). A new credential on every rotation would mean a new token.
        graph.AddHttpMessageHandler(provider => new GraphAuthenticationHandler(provider.GetRequiredService<GraphCredential>().Credential));

        // Channels: Azure Communication Services with a managed identity.
        services.AddSingleton(provider => new EmailClient(
            provider.GetRequiredService<IOptions<CommunicationOptions>>().Value.Endpoint,
            provider.GetRequiredService<TokenCredential>()));
        services.AddSingleton(provider => new SmsClient(
            provider.GetRequiredService<IOptions<CommunicationOptions>>().Value.Endpoint,
            provider.GetRequiredService<TokenCredential>()));
        services.AddSingleton<IEmailSender, AcsEmailSender>();
        services.AddSingleton<ISmsSender, AcsSmsSender>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFailureClassifier, AzureRequestFailureClassifier>());

        // Invoice links: user delegation SAS through Front Door (a factory shared from BuildingBlocks).
        // AZURE: the identity needs Storage Blob Data Reader on the invoices container and Storage Blob Delegator on the account.
        // It has no write access: Payments creates the file, Notifications only issues the link.
        services.AddAzureClients(clients => clients.AddBlobServiceClient(configuration.GetSection(InvoiceLinkOptions.SectionName)));
        services.AddSingleton<BlobReadLinkFactory>();
        services.AddSingleton<IInvoiceLinks, BlobInvoiceLinks>();

        services.AddSingleton<INotificationFeatures, NotificationFeatures>();

        services.AddHealthChecks().Add(new HealthCheckRegistration(
            "cosmos-notification-log",
            provider => new CosmosContainerHealthCheck(provider.GetRequiredService<Database>().GetContainer(NotificationsContainers.LogName)),
            HealthStatus.Unhealthy,
            [HealthTags.Dependencies]));

        return services;
    }
}

internal sealed record GraphCredential(TokenCredential Credential);
