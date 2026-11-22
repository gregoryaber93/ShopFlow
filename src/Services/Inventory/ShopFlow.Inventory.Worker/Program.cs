using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using ShopFlow.Azure;
using ShopFlow.Contracts;
using ShopFlow.Contracts.Inventory.V1;
using ShopFlow.Contracts.Orders.V1;
using ShopFlow.Inventory.Application;
using ShopFlow.Inventory.Application.Reservations;
using ShopFlow.Inventory.Infrastructure;
using ShopFlow.Inventory.Worker;
using ShopFlow.Messaging;
using ShopFlow.Messaging.ServiceBus;
using ShopFlow.Observability;

// AZURE: inventory-worker is a Container App without ingress, scaled by KEDA. The "azure-servicebus" scale rule
// (infra/modules/compute/container-app.bicep) checks the number of messages in the inventory-commands queue every few
// seconds and sets the number of replicas to ceil(length / messageCount), within minReplicas..maxReplicas. KEDA
// authenticates to Service Bus with the application's managed identity, not a connection string.
//
// WHY: messageCount in the rule = MaxConcurrentCalls below (8). A replica handles 8 messages at a time, so with 80
// waiting KEDA starts 10 replicas. If these numbers diverge you get either idle replicas or a queue that grows despite
// free capacity.
//
// WHY: minReplicas = 1, not 0, even though KEDA allows scaling to zero. The same process publishes the outbox (events
// written by inventory-api, e.g. a price change) and expires reservations. With zero replicas nothing would do that
// until a command arrives.

var builder = WebApplication.CreateBuilder(args);

builder.AddShopFlowAzure();
builder.AddShopFlowObservability("inventory-worker");

var maxConcurrentCalls = builder.Configuration.GetValue("Worker:MaxConcurrentCalls", 8);

builder.Services.AddInventoryMessaging()
    .UseAzureServiceBus(builder.Configuration)
    .AddHandler<ReserveStock, ReserveStockHandler>()
    .AddHandler<ReleaseStock, ReleaseStockHandler>()
    .AddHandler<OrderConfirmed, OrderConfirmedHandler>()
    .AddServiceBusConsumer(consumer =>
    {
        consumer.EntityPath = MessagingTopology.Queues.InventoryCommands;
        consumer.Endpoint = new ConsumerEndpoint(MessagingTopology.Consumers.InventoryWorker, UnhandledMessagePolicy.DeadLetter);
        consumer.MaxConcurrentCalls = maxConcurrentCalls;
        consumer.PrefetchCount = maxConcurrentCalls;
    });

builder.Services.AddInventoryApplication(builder.Configuration);
builder.Services.AddInventoryInfrastructure(builder.Configuration, runOutboxPoller: true);
builder.Services.AddHostedService<ReservationExpiryService>();

// AZURE: on scale-in and on a new revision Container Apps sends SIGTERM, waits terminationGracePeriodSeconds (30 s)
// and then kills the container. The host has 25 s to stop the consumer (finish in-flight messages) and the poller,
// leaving headroom before the hard kill.
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(25));

builder.Services.AddHealthChecks().AddLifecycleCheck();

var app = builder.Build();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(HealthTags.Ready) });

await app.RunAsync();
