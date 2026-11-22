using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.FeatureManagement;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Payments.V1;
using ShopFlow.Messaging;
using ShopFlow.Messaging.Failures;
using ShopFlow.Observability;
using ShopFlow.Payments.Application.Abstractions;
using ShopFlow.Payments.Infrastructure.Cosmos;
using ShopFlow.Payments.Infrastructure.Invoicing;
using ShopFlow.Payments.Infrastructure.Psp;
using ShopFlow.Persistence.Cosmos;
using ShopFlow.Resilience;

namespace ShopFlow.Payments.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPaymentsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddShopFlowCosmos(configuration);
        services.AddSingleton<PaymentsContainers>();
        services.AddScoped<IPaymentRepository, CosmosPaymentRepository>();
        services.AddSingleton<IInvoiceNumberAllocator, CosmosInvoiceNumberAllocator>();
        services.AddSingleton<IPaymentFeatures, PaymentFeatures>();

        AddInvoiceStorage(services, configuration);
        AddPaymentProvider(services, configuration);

        services.AddHealthChecks().Add(new HealthCheckRegistration(
            "cosmos-payments",
            provider => new CosmosContainerHealthCheck(provider.GetRequiredService<PaymentsContainers>().Payments),
            HealthStatus.Unhealthy,
            [HealthTags.Dependencies]));

        return services;
    }

    public static MessagingBuilder AddPaymentsMessaging(this IServiceCollection services) =>
        services.AddShopFlowMessaging(MessagingTopology.Sources.Payments)
            .Route<PaymentAuthorized>(MessagingTopology.Topics.PaymentsEvents)
            .Route<PaymentDeclined>(MessagingTopology.Topics.PaymentsEvents)
            .Route<PaymentCaptured>(MessagingTopology.Topics.PaymentsEvents)
            .Route<PaymentCaptureFailed>(MessagingTopology.Topics.PaymentsEvents)
            .Route<PaymentVoided>(MessagingTopology.Topics.PaymentsEvents)
            .Route<PaymentRefunded>(MessagingTopology.Topics.PaymentsEvents)
            .Route<InvoiceIssued>(MessagingTopology.Topics.PaymentsEvents);

    private static void AddInvoiceStorage(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(InvoiceStorageOptions.SectionName);
        services.AddOptions<InvoiceStorageOptions>().Bind(section).ValidateDataAnnotations().ValidateOnStart();

        // AZURE: the Blob Storage client from the Microsoft.Extensions.Azure registry: the address from the section (serviceUri
        // key), the credential (managed identity) from UseCredential in ShopFlow.Azure.
        services.AddAzureClients(clients => clients.AddBlobServiceClient(section));

        services.AddSingleton<IInvoiceRenderer, SimplePdfInvoiceRenderer>();
        services.AddSingleton<IInvoiceStorage, BlobInvoiceStorage>();
    }

    private static void AddPaymentProvider(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<PspOptions>()
            .Bind(configuration.GetSection(PspOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var baseUrl = configuration.GetSection(PspOptions.SectionName).Get<PspOptions>()?.BaseUrl;

        services.AddTransient<PspAuthenticationHandler>();
        var client = services.AddHttpClient<IPaymentGateway, PspGatewayClient>(http =>
        {
            http.BaseAddress = baseUrl;
            http.Timeout = Timeout.InfiniteTimeSpan;
        });

        // Order: resilience (retry, circuit breaker, concurrency limit) first, then authentication on every attempt.
        client.AddPaymentProviderResilience();
        client.AddHttpMessageHandler<PspAuthenticationHandler>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PspWebhookVerifier>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IFailureClassifier, PaymentGatewayFailureClassifier>());
    }
}

internal sealed class PaymentFeatures(IFeatureManager featureManager) : IPaymentFeatures
{
    public Task<bool> ForceThreeDSecureAsync() => featureManager.IsEnabledAsync(PaymentFeatureFlags.ForceThreeDSecure);
}
