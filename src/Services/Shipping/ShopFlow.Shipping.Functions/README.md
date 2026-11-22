# shipping-processor

Azure Functions (isolated worker, .NET 10), **Flex Consumption** plan, VNet + NAT Gateway.

| Function | Trigger | Role |
|---|---|---|
| `shipping-commands` | queue `shipping-commands` (no sessions) | `CreateShipment` v1 and v2, `CancelShipment` |
| `shipping-outbox-relay` | Change Feed of the `shipments` container | publishing to `shipping-events` |
| `carrier-webhook` | HTTP `POST /api/webhooks/carrier` through APIM | `ShipmentDispatched`, `ShipmentDelivered` |
| `shipment-label` | HTTP `GET /api/shipments/{orderId}/{shipmentId}/label` | 302 to a SAS link valid for 15 minutes (role `Warehouse.Operator`) |

New compared with the previous services:

- **`maxConcurrentCalls: 8` in host.json.** The bottleneck is the carrier API's request limit, not compute power. Lower concurrency per instance (together with `maximumInstanceCount` in Bicep) reduces the number of 429 responses.
- **User delegation SAS** ([BlobReadLinkFactory](../../../BuildingBlocks/ShopFlow.Azure/Storage/BlobReadLinkFactory.cs)): a file link signed with a key obtained with an Entra ID identity, not with the account key, which is disabled. The link points at Front Door, because the Storage account has no public access.
- **Partner authentication at the gateway** ([ShippingFunctions](ShippingFunctions.cs)): APIM verifies the carrier's token from Key Vault and forwards the request with its own Entra ID token, so the function does not know the carrier's secret.

| Setting | Example |
|---|---|
| `Cosmos__accountEndpoint`, `Cosmos__DatabaseName` | `...`, `shipping` |
| `ServiceBus__fullyQualifiedNamespace` | `sbns-shopflow-prod-weu.servicebus.windows.net` |
| `LabelStorage__serviceUri` | `https://stshopflowdocsprodweu.blob.core.windows.net` |
| `BlobLinks__PublicBaseUrl` | `https://files.shopflow.example` (Front Door) |
| `Carrier__BaseUrl`, `Carrier__CarrierCode` | `https://api.carrier.example/`, `DPD` |
| `Carrier:ApiKey` (App Configuration → Key Vault) | secret `carrier-api-key` |

Walkthrough: [04-shipping-delivery-invoice.md](../../../../docs/walkthroughs/04-shipping-delivery-invoice.md).
