using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Inventory.V1;
using ShopFlow.Contracts.Orders.V1;
using ShopFlow.Contracts.Payments.V1;
using ShopFlow.Contracts.Shipping.V1;
using ShopFlow.Messaging;
using ShopFlow.OrderSaga.Activities;
using ShopFlow.OrderSaga.Messaging;
using ShopFlow.OrderSaga.Model;
using ShopFlow.Persistence.Cosmos;
using V2 = ShopFlow.Contracts.Orders.V2;

namespace ShopFlow.OrderSaga;

public static class DependencyInjection
{
    /// <summary>Saga handlers, options and activities. The host adds the broker (UseAzureServiceBus) and the HTTP client.</summary>
    public static MessagingBuilder AddOrderSaga(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SagaOptions>()
            .Bind(configuration.GetSection(SagaOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddShopFlowCosmos(configuration);
        services.AddSingleton<SagaStateStore>();
        services.AddSingleton<SagaCommandSender>();
        services.AddScoped<DurableClientHolder>();

        return services.AddShopFlowMessaging(MessagingTopology.Sources.OrderSaga)
            .AddHandler<V2.OrderSubmitted, StartSagaHandler>()
            .AddHandler<StockReserved, SagaReplyHandler>()
            .AddHandler<StockReservationFailed, SagaReplyHandler>()
            .AddHandler<StockReleased, SagaReplyHandler>()
            .AddHandler<PaymentAuthorized, SagaReplyHandler>()
            .AddHandler<PaymentDeclined, SagaReplyHandler>()
            .AddHandler<PaymentCaptured, SagaReplyHandler>()
            .AddHandler<PaymentCaptureFailed, SagaReplyHandler>()
            .AddHandler<PaymentVoided, SagaReplyHandler>()
            .AddHandler<PaymentRefunded, SagaReplyHandler>()
            .AddHandler<ShipmentCreated, SagaReplyHandler>()
            .AddHandler<ShipmentCreationFailed, SagaReplyHandler>()
            .AddHandler<ShipmentCancelled, SagaReplyHandler>()
            .AddHandler<OrderCancellationRequested, SagaReplyHandler>();
    }
}
