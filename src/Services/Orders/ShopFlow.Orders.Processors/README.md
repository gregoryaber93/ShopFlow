# orders-processors

A function app (Azure Functions, isolated worker, .NET 10) on the **Flex Consumption** plan.

| Function | Trigger | Role |
|---|---|---|
| `orders-outbox-relay` | Change Feed of the `orders` container | publishes outbox records to the `orders-events` topic |
| `shipment-events` | subscription `shipping-events/orders` | `ShipmentDispatched`, `ShipmentDelivered`, i.e. order status changes |

## Application settings (deployed by Bicep)

JSON does not allow comments, so the settings are described here.

| Setting | Example | Meaning |
|---|---|---|
| `AzureWebJobsStorage__accountName` | `stshopflowfuncprodweu` | the Functions host's Storage account (host leases, trigger state); no connection string |
| `AzureWebJobsStorage__credential` | `managedidentity` | the host connects to Storage with the managed identity |
| `AzureWebJobsStorage__clientId` | `<client id>` | which user-assigned identity (an app can have several) |
| `Cosmos__accountEndpoint` | `https://cosmos-shopflow-prod-weu.documents.azure.com:443/` | the Cosmos DB account for the trigger and for the client in code |
| `Cosmos__credential`, `Cosmos__clientId` | `managedidentity`, `<client id>` | as above for the Cosmos DB trigger |
| `ServiceBus__fullyQualifiedNamespace` | `sbns-shopflow-prod-weu.servicebus.windows.net` | the namespace for the trigger and the publisher |
| `ServiceBus__credential`, `ServiceBus__clientId` | `managedidentity`, `<client id>` | as above for the Service Bus trigger |
| `AZURE_CLIENT_ID` | `<client id>` | the identity for application code (`ManagedIdentityCredential`) |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | `InstrumentationKey=...;IngestionEndpoint=...` | host and worker telemetry |
| `APPLICATIONINSIGHTS_AUTHENTICATION_STRING` | `ClientId=<client id>;Authorization=AAD` | the host sends telemetry with an Entra ID token |

**AZURE:** the `<Connection>__<property>` convention is shared by all Functions extensions. When
`__fullyQualifiedNamespace` or `__accountEndpoint` exists and there is no connection string, the extension uses an
identity. The section names in code (`ServiceBusOptions`, `CosmosOptions`) deliberately follow the same convention, so
one address configures both the trigger and our own client.

## host.json

- `telemetryMode: OpenTelemetry`: the host and the worker produce one distributed trace (see `ShopFlow.Functions`).
- `serviceBus.autoCompleteMessages: false`: code settles the messages (`ServiceBusTriggerAdapter`), so that they can be
  dead-lettered deliberately with a reason.
- `serviceBus.prefetchCount: 0`: with Flex Consumption instances come and go. Messages prefetched by an instance that
  is shutting down would wait for their locks to expire.
- `serviceBus.maxConcurrentCalls: 16`: messages processed in parallel on one instance. Together with the instance cap
  (`maximumInstanceCount` in Bicep) it sets the maximum load on Cosmos DB.
- `serviceBus.maxAutoLockRenewalDuration`: the host renews the message lock while the handler works. This time must also
  cover the delay before a retry (`ServiceBusMessageProcessor`).

## Deployment

Flex Consumption has no deployment slots. Instead of a swap we use a **rolling update**: the platform replaces instances
in batches, and in-flight invocations finish on the old version. The consequence for the code: two versions run side by
side for a few minutes. That is why contract and document changes must be backward compatible (see
`OrderDocument.SchemaVersion` and docs/adr/0014).
