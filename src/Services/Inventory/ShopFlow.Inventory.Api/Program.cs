using System.Text.Json;
using System.Text.Json.Serialization;
using ShopFlow.Azure;
using ShopFlow.Inventory.Api.Endpoints;
using ShopFlow.Inventory.Application;
using ShopFlow.Inventory.Infrastructure;
using ShopFlow.Web;

// AZURE: inventory-api runs in Azure Container Apps, in an internal environment (no public IP).
// Traffic comes from two sides: from API Management (the catalog for customers) and from orders-api (prices for
// checkout). The container's ingress listens on port 8080 (the default port of .NET 8+ images for a non-root
// user). The HTTP scale rule (infra/modules/compute/container-app.bicep) adds replicas when the number of concurrent
// requests per replica exceeds the threshold. The catalog has traffic spikes during campaigns, and Container Apps
// scales within seconds and drops to the minimum at night.
//
// Every deployment creates a new revision (immutable image + configuration). In Single mode traffic moves to it
// once its replicas pass the readiness probe, and a rollback is a new revision with the previous image
// (rollback.yml). Weighted traffic splitting (Multiple mode) is described as an alternative in docs/adr/0014.

var builder = WebApplication.CreateBuilder(args);

builder.AddShopFlowAzure();
builder.AddShopFlowWebDefaults("inventory-api");

builder.Services.AddInventoryMessaging();
builder.Services.AddInventoryApplication(builder.Configuration);
builder.Services.AddInventoryInfrastructure(builder.Configuration, runOutboxPoller: false);
builder.Services.AddCatalogCache(builder.Configuration);

builder.Services.AddAuthorization(InventoryPolicies.Configure);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

builder.Services.AddOpenApi("v1");
builder.Services.AddOpenApi("internal");

var app = builder.Build();

app.UseShopFlowWebDefaults();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapInventoryEndpoints();

await app.RunAsync();

/// <summary>Anchor for WebApplicationFactory in component tests.</summary>
public partial class Program;
