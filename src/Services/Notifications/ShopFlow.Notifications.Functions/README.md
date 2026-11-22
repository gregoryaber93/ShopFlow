# notifications-processor

Azure Functions (isolated worker, .NET 10), **Flex Consumption** plan, VNet + NAT Gateway.

| Function | Trigger | Role |
|---|---|---|
| `notifications-inbox` | queue `notifications-inbox` (no sessions) | customer e-mails, parcel dispatch SMS, warehouse alerts |
| `notification-history` | HTTP `GET /api/customers/{customerId}/notifications` | send history for customer support (role `Support.Reader`) |

| Event | Notification |
|---|---|
| `OrderConfirmed`, `OrderRejected`, `OrderCancelled` | e-mail to the customer |
| `PaymentRefunded` | e-mail, but not for a refund of 0 (a released hold) |
| `InvoiceIssued` | e-mail with a SAS link to the invoice, valid for 72 hours |
| `ShipmentDispatched` | e-mail and an SMS behind the `Notifications.ShipmentSms` flag (targeting) |
| `ShipmentDelivered` | e-mail |
| `StockLevelLow` | e-mail to the operations team (in English), at most one per SKU, warehouse and day |

Customer e-mails are localized (`pl` and `en`) in [NotificationTemplates](../ShopFlow.Notifications.Application/Templates/NotificationTemplates.cs): the Polish strings there are customer-facing content for pl-PL customers, not documentation.

New compared with the previous services:

- **Idempotency of a side effect outside the database** ([NotificationSender](../ShopFlow.Notifications.Application/NotificationSender.cs), [CosmosNotificationLog](../ShopFlow.Notifications.Infrastructure/Cosmos/CosmosNotificationLog.cs)). An e-mail cannot be committed in one transaction with an inbox entry. The send history therefore has three steps: a reservation right before calling the provider, the send with a deterministic operation identifier, recording the outcome. A reservation found later means the previous attempt may have reached ACS, so the adapter first asks about the operation's status.
- **Two different guarantees for two channels.** E-mail works "at least once" and checks the status of the previous operation in ACS. SMS works "at most once", because the API does not allow checking a send by our identifier, and the customer got the e-mail anyway.
- **Identity federation across tenants** ([GraphCustomerDirectory](../ShopFlow.Notifications.Infrastructure/Customers/GraphCustomerDirectory.cs)). The customer's address is in the External ID tenant, and the managed identity in the company tenant. The managed identity token is the assertion for the app registration in the customers tenant, so there is no secret at all.
- **Personal data outside events.** Events carry only `CustomerId`. The address is fetched from Graph at send time and kept for 15 minutes in process memory (never in Redis). The history stores a masked address.
- **A feature flag with targeting.** The SMS decision depends on the customer (a hash of the identifier), so it is stable across instances and retries.
- **`maxConcurrentCalls: 4`.** A new ACS Email resource has low default send limits. Low concurrency (together with `maximumInstanceCount` in Bicep) turns a traffic spike into a queue instead of an avalanche of 429 responses.
- **Emergency stop of sending** (e.g. a bug in a template): the app setting `AzureWebJobs.notifications-inbox.Disabled = true` stops the trigger without a deployment. Messages wait in the queue and are processed from where they stopped after the fix.

| Setting | Example |
|---|---|
| `Cosmos__accountEndpoint`, `Cosmos__DatabaseName` | `...`, `notifications` |
| `ServiceBus__fullyQualifiedNamespace` | `sbns-shopflow-prod-weu.servicebus.windows.net` |
| `Communication__Endpoint` | `https://acs-shopflow-prod.europe.communication.azure.com` |
| `Communication__EmailSender`, `Communication__EmailReplyTo` | `DoNotReply@notify.shopflow.example`, `support@shopflow.example` |
| `Communication__SmsSender` | an E.164 number purchased in ACS |
| `CustomerDirectory__TenantId`, `CustomerDirectory__ClientId` | the External ID tenant and the app registration with the federated credential |
| `InvoiceLinks__serviceUri` | `https://stshopflowdocsprodweu.blob.core.windows.net` |
| `BlobLinks__PublicBaseUrl` | `https://files.shopflow.example` (Front Door) |
| `Notifications__ShopBaseUrl`, `Notifications__OperationsEmail` | `https://shopflow.example/`, the operations team distribution list |
| `Notifications__CarrierTrackingUrls__DPD` | a template with `{trackingNumber}` |
