using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using ShopFlow.Azure;
using ShopFlow.Functions;
using ShopFlow.Messaging.ServiceBus;
using ShopFlow.Persistence.Cosmos;
using ShopFlow.Shipping.Application;
using ShopFlow.Shipping.Infrastructure;
using V1 = ShopFlow.Contracts.Shipping.V1;
using V2 = ShopFlow.Contracts.Shipping.V2;

// AZURE: shipping-processor, Flex Consumption with VNet integration and a NAT Gateway (the carrier also requires an
// IP allowlist). Identity: Data Receiver on shipping-commands, Data Sender on shipping-events, Data Contributor on the
// shipping database in Cosmos DB, Storage Blob Data Contributor on the labels container, and Storage Blob Delegator on
// the account (issuing SAS links to labels).

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();
builder.AddShopFlowAzure();
builder.AddShopFlowFunctionsDefaults("shipping-processor");

builder.Services.AddShippingMessaging()
    .UseAzureServiceBus(builder.Configuration)
    .AddHandler<V1.CreateShipment, CreateShipmentHandler>()
    .AddHandler<V2.CreateShipment, CreateShipmentHandler>()
    .AddHandler<V1.CancelShipment, CancelShipmentHandler>();

builder.Services.AddShippingApplication();
builder.Services.AddShippingInfrastructure(builder.Configuration);
builder.Services.AddShopFlowCosmosOutboxRelay();

await builder.Build().RunAsync();
