using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using ShopFlow.Azure;
using ShopFlow.CustomerReadModels.Application;
using ShopFlow.CustomerReadModels.Infrastructure;
using ShopFlow.Functions;
using ShopFlow.Messaging.ServiceBus;

// AZURE: readmodels-projections, Flex Consumption with VNet integration. The managed identity has:
// - Azure Service Bus Data Receiver on the readmodels-inbox queue,
// - Cosmos DB Built-in Data Contributor on the readmodels database (other services' databases are out of its reach),
// - access to Azure Managed Redis (access policy assignment) to invalidate rm:order:* keys.

var builder = FunctionsApplication.CreateBuilder(args);

builder.AddShopFlowAzure();
builder.AddShopFlowFunctionsDefaults("readmodels-projections");

builder.Services.AddCustomerReadModelsMessaging()
    .UseAzureServiceBus(builder.Configuration)
    .AddCustomerOrderProjection();

builder.Services.AddCustomerReadModelsApplication();
builder.Services.AddCustomerOrderStore(builder.Configuration, "readmodels-projections");
builder.Services.AddOrderDetailsCache(builder.Configuration);

await builder.Build().RunAsync();
