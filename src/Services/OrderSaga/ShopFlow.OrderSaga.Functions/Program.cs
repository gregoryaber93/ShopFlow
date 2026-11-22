using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ShopFlow.Azure;
using ShopFlow.Functions;
using ShopFlow.Messaging.ServiceBus;
using ShopFlow.OrderSaga;
using ShopFlow.OrderSaga.Activities;
using ShopFlow.Resilience;

// AZURE: order-saga is a Durable Functions app on the Flex Consumption plan. The orchestration state (history,
// timers, events) is stored by the Durable Task Scheduler: a separate managed service with its own dashboard for
// inspecting instances. The app connects to it with a managed identity (Durable Task Data Contributor role on the
// task hub). Advantages over the default Azure Storage backend: higher throughput, no queues and tables to maintain,
// a built-in dashboard.

var builder = FunctionsApplication.CreateBuilder(args);

// ASP.NET Core integration for HTTP functions (operator panel).
builder.ConfigureFunctionsWebApplication();

builder.AddShopFlowAzure();
builder.AddShopFlowFunctionsDefaults("order-saga");

// The saga sends commands to three queues: Azure Service Bus Data Sender role on inventory-commands,
// payments-commands and shipping-commands. It receives from saga-replies and orders-events/saga-start.
builder.Services.AddOrderSaga(builder.Configuration).UseAzureServiceBus(builder.Configuration);

builder.Services.AddOptions<OrdersApiOptions>()
    .Bind(builder.Configuration.GetSection(OrdersApiOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

var ordersApi = builder.Configuration.GetSection(OrdersApiOptions.SectionName).Get<OrdersApiOptions>() ?? new OrdersApiOptions();
var ordersClient = builder.Services.AddHttpClient<OrdersApiClient>(http =>
{
    http.BaseAddress = ordersApi.BaseUrl;
    http.Timeout = Timeout.InfiniteTimeSpan;
});
ordersClient.AddInternalServiceResilience();
ordersClient.AddServiceToServiceAuthentication(ordersApi.Scope);

await builder.Build().RunAsync();
