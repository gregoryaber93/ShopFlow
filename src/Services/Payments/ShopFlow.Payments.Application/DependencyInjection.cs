using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShopFlow.Payments.Application.Invoicing;
using ShopFlow.Payments.Application.Webhooks;

namespace ShopFlow.Payments.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddPaymentsApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<PaymentEventPublisher>();
        services.AddScoped<PspWebhookProcessor>();
        services.AddScoped<InvoiceRegistrationService>();
        return services;
    }
}
