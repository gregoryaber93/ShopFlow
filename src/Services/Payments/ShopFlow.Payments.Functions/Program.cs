using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Hosting;
using ShopFlow.Azure;
using ShopFlow.Contracts.Payments.V1;
using ShopFlow.Functions;
using ShopFlow.Messaging.ServiceBus;
using ShopFlow.Payments.Application;
using ShopFlow.Payments.Application.Commands;
using ShopFlow.Payments.Application.Invoicing;
using ShopFlow.Payments.Infrastructure;
using ShopFlow.Persistence.Cosmos;

// AZURE: payments-processor is a Flex Consumption function app with VNet integration and a NAT Gateway (a fixed
// outbound address for the payment provider). Its identity's permissions:
//   - Service Bus: Data Receiver on payments-commands, payments-events/invoicing, invoice-blob-events;
//     Data Sender on payments-events,
//   - Cosmos DB: Data Contributor on the payments database,
//   - Blob Storage: Data Contributor on the invoices container,
//   - Key Vault: Secrets User (payment provider secrets, through references in App Configuration).

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();
builder.AddShopFlowAzure();
builder.AddShopFlowFunctionsDefaults("payments-processor");

builder.Services.AddPaymentsMessaging()
    .UseAzureServiceBus(builder.Configuration)
    .AddHandler<AuthorizePayment, AuthorizePaymentHandler>()
    .AddHandler<CapturePayment, CapturePaymentHandler>()
    .AddHandler<VoidPayment, VoidPaymentHandler>()
    .AddHandler<RefundPayment, RefundPaymentHandler>()
    .AddHandler<PaymentCaptured, InvoiceGenerationHandler>();

builder.Services.AddPaymentsApplication();
builder.Services.AddPaymentsInfrastructure(builder.Configuration);
builder.Services.AddShopFlowCosmosOutboxRelay();

await builder.Build().RunAsync();
