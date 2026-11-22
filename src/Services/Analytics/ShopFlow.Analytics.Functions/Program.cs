using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using ShopFlow.Analytics.Application;
using ShopFlow.Analytics.Infrastructure;
using ShopFlow.Azure;
using ShopFlow.Functions;

// AZURE: analytics-processor, Flex Consumption with VNet integration. The managed identity has:
// - Azure Service Bus Data Receiver on the analytics-inbox queue,
// - Azure Event Hubs Data Sender on the clickstream and business-events hubs,
// - Azure Event Hubs Data Receiver on those hubs (triggers in their consumer groups),
// - Storage Blob Data Owner on the function host storage account (Event Hubs checkpoints and partition leases),
// - Cosmos DB Built-in Data Contributor on the analytics database.
// Flex Consumption scaling for the Event Hubs trigger: at most one instance per hub partition,
// because within a consumer group only one reader reads a partition at a time.

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();
builder.AddShopFlowAzure();
builder.AddShopFlowFunctionsDefaults("analytics-processor");

builder.Services.AddAnalyticsApplication();
builder.Services.AddAnalyticsInfrastructure(builder.Configuration);

await builder.Build().RunAsync();
