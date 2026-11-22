using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShopFlow.Orders.Application.Carts;
using ShopFlow.Orders.Application.Orders;

namespace ShopFlow.Orders.Application;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the Orders use cases.
    /// </summary>
    /// <remarks>
    /// WHY: no mediator. An endpoint gets a concrete handler through DI and calls it directly, so "go to definition"
    /// in the IDE leads from the HTTP request to the logic in one step.
    /// </remarks>
    public static IServiceCollection AddOrdersApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<OrderEventPublisher>();
        services.AddScoped<OrderCommandExecutor>();
        services.AddScoped<SubmitOrderHandler>();
        services.AddScoped<GetOrderHandler>();
        services.AddScoped<RequestCancellationHandler>();
        services.AddScoped<SagaOrderOperations>();
        services.AddScoped<CartService>();

        services.AddValidatorsFromAssemblyContaining<SubmitOrderCommandValidator>(ServiceLifetime.Singleton);

        return services;
    }
}
