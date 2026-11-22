using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using ShopFlow.Azure;
using ShopFlow.Contracts.Shipping.V1;
using ShopFlow.Functions;
using ShopFlow.Messaging.ServiceBus;
using ShopFlow.Orders.Application;
using ShopFlow.Orders.Application.Shipping;
using ShopFlow.Orders.Infrastructure;
using ShopFlow.Persistence.Cosmos;

// AZURE: orders-processors runs on the Flex Consumption plan: it scales from zero to many instances depending
// on the number of Change Feed changes and the Service Bus subscription length, and you pay for execution time.
// The plan supports VNet integration, so functions reach Cosmos DB and Service Bus through private endpoints even
// though both resources have public access disabled.

var builder = FunctionsApplication.CreateBuilder(args);

builder.AddShopFlowAzure();
builder.AddShopFlowFunctionsDefaults("orders-processors");

builder.Services.AddOrdersMessaging()
    .UseAzureServiceBus(builder.Configuration)
    .AddHandler<ShipmentDispatched, ShipmentDispatchedHandler>()
    .AddHandler<ShipmentDelivered, ShipmentDeliveredHandler>();

builder.Services.AddOrdersApplication();
builder.Services.AddOrdersInfrastructure(builder.Configuration);

// Only this process publishes Orders events: it has the Azure Service Bus Data Sender role on the orders-events topic.
builder.Services.AddShopFlowCosmosOutboxRelay();

await builder.Build().RunAsync();
