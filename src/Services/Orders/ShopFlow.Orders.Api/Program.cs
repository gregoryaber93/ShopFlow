using System.Text.Json;
using System.Text.Json.Serialization;
using ShopFlow.Azure;
using ShopFlow.Orders.Api.Endpoints;
using ShopFlow.Orders.Api.Http;
using ShopFlow.Orders.Api.Idempotency;
using ShopFlow.Orders.Application;
using ShopFlow.Orders.Infrastructure;
using ShopFlow.Web;
using ShopFlow.Web.Idempotency;

// AZURE: orders-api runs on App Service (Linux Premium v3 plan) with two slots: production and staging.
// The pipeline deploys the package to the staging slot, waits for /health/ready, runs a smoke test and swaps.
// The swap switches traffic without a cold start because the staging slot is already warm, and going back to
// the previous version is another swap (see docs/runbooks/deployment-rollback.md).
//
// Configuration comes from three sources, in override order:
//   appsettings.json → App Service application settings (environment variables, e.g. Cosmos__AccountEndpoint,
//   set by Bicep) → Azure App Configuration (Orders:* and Shared:* keys, feature flags).
// None of them contains a secret: there are only service addresses here, and the managed identity grants access.

var builder = WebApplication.CreateBuilder(args);

builder.AddShopFlowAzure();
builder.AddShopFlowWebDefaults("orders-api");

// WHY: orders-api registers event routes (so the outbox knows which topic a message goes to), but it does not
// register the broker (UseAzureServiceBus). It has no Service Bus client and no Service Bus role at all:
// events leave the outbox through orders-processors.
builder.Services.AddOrdersMessaging();
builder.Services.AddOrdersApplication();
builder.Services.AddOrdersInfrastructure(builder.Configuration);
builder.Services.AddCatalogPricing(builder.Configuration);
builder.Services.AddScoped<IIdempotencyStore, CosmosIdempotencyStore>();

builder.Services.AddAuthorization(OrdersPolicies.Configure);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

// Separate OpenAPI documents for each version. The v1 and v2 documents describe the public API (APIM
// operations are defined in infra/modules/edge/apim.bicep), while "internal" is only for the saga team.
builder.Services.AddOpenApi("v1");
builder.Services.AddOpenApi("v2");
builder.Services.AddOpenApi("internal");

var app = builder.Build();

app.UseShopFlowWebDefaults();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapPublicOrderEndpoints();
app.MapCartEndpoints();
app.MapInternalOrderEndpoints();

await app.RunAsync();

/// <summary>Anchor for WebApplicationFactory in component tests.</summary>
public partial class Program;
