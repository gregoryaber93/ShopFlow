using ShopFlow.Azure;
using ShopFlow.CustomerReadModels.Api;
using ShopFlow.CustomerReadModels.Application;
using ShopFlow.CustomerReadModels.Infrastructure;
using ShopFlow.Web;

// AZURE: readmodels-api runs in Azure Container Apps (internal environment), behind API Management. It is the CQRS read
// side: it scales independently of the write side (an HTTP rule on the number of concurrent requests), because "my
// orders" traffic grows after campaigns and after every placed order (the application polls the status), and orders-api
// does not have to serve it. The managed identity has read access only: Cosmos DB Built-in Data Reader on the readmodels
// database, access to Redis, Storage Blob Data Reader on invoices and Storage Blob Delegator on the account.
//
// WHY: the API has no Service Bus role and no write access to Cosmos DB. Even if compromised it cannot change order
// state; the worst it can do is read the views (and only through Data Reader on one database).

var builder = WebApplication.CreateBuilder(args);

builder.AddShopFlowAzure();
builder.AddShopFlowWebDefaults("readmodels-api");

builder.Services.AddCustomerReadModelsApplication();
builder.Services.AddCustomerOrderStore(builder.Configuration, "readmodels-api");
builder.Services.AddOrderDetailsCache(builder.Configuration);
builder.Services.AddInvoiceDownloads(builder.Configuration);

builder.Services.AddAuthorization(ReadModelsPolicies.Configure);
builder.Services.AddOpenApi("v1");

var app = builder.Build();

app.UseShopFlowWebDefaults();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapCustomerOrderEndpoints();

await app.RunAsync();

/// <summary>Anchor for WebApplicationFactory in component tests.</summary>
public partial class Program;
