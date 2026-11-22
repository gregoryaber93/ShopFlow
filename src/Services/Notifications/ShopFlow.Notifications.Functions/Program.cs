using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using ShopFlow.Azure;
using ShopFlow.Functions;
using ShopFlow.Messaging.ServiceBus;
using ShopFlow.Notifications.Application;
using ShopFlow.Notifications.Infrastructure;
using InventoryV1 = ShopFlow.Contracts.Inventory.V1;
using OrdersV1 = ShopFlow.Contracts.Orders.V1;
using PaymentsV1 = ShopFlow.Contracts.Payments.V1;
using ShippingV1 = ShopFlow.Contracts.Shipping.V1;

// AZURE: notifications-processor, Flex Consumption with VNet integration and a NAT Gateway. The managed identity has:
// - Azure Service Bus Data Receiver on the notifications-inbox queue (it publishes nothing),
// - Cosmos DB Built-in Data Contributor on the notifications database,
// - Storage Blob Data Reader on the invoices container and Storage Blob Delegator on the documents account,
// - Communication and Email Service Owner on the ACS resource,
// - App Configuration Data Reader,
// - a federated credential on the app registration in the External ID tenant (reading customer profiles from Graph).

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();
builder.AddShopFlowAzure();
builder.AddShopFlowFunctionsDefaults("notifications-processor");

builder.Services.AddNotificationsMessaging()
    .UseAzureServiceBus(builder.Configuration)
    .AddHandler<OrdersV1.OrderConfirmed, OrderNotificationHandlers>()
    .AddHandler<OrdersV1.OrderRejected, OrderNotificationHandlers>()
    .AddHandler<OrdersV1.OrderCancelled, OrderNotificationHandlers>()
    .AddHandler<PaymentsV1.PaymentRefunded, PaymentNotificationHandlers>()
    .AddHandler<PaymentsV1.InvoiceIssued, PaymentNotificationHandlers>()
    .AddHandler<ShippingV1.ShipmentDispatched, ShipmentNotificationHandlers>()
    .AddHandler<ShippingV1.ShipmentDelivered, ShipmentNotificationHandlers>()
    .AddHandler<InventoryV1.StockLevelLow, StockAlertHandler>();

builder.Services.AddNotificationsApplication();
builder.Services.AddNotificationsInfrastructure(builder.Configuration);

await builder.Build().RunAsync();
