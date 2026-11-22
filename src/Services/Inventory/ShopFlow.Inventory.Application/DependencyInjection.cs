using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShopFlow.Inventory.Application.Abstractions;
using ShopFlow.Inventory.Application.Catalog;
using ShopFlow.Inventory.Application.Reservations;

namespace ShopFlow.Inventory.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddInventoryApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<ReservationOptions>().Bind(configuration.GetSection(ReservationOptions.SectionName));

        services.AddScoped<InventoryEventPublisher>();
        services.AddScoped<BackofficeOperations>();
        services.AddScoped<ExpireReservationHandler>();

        return services;
    }
}
