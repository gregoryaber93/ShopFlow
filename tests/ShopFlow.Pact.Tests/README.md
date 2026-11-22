# Consumer–provider contracts (Pact)

| Pact | Consumer (code under test) | Provider (code under test) |
|---|---|---|
| [orders-api → inventory-api](pacts/orders-api-inventory-api.json) | `CatalogPricingClient` (HTTP) | inventory-api on Kestrel, provider states through `/provider-states` |
| [orders-processors ← shipping-processor](pacts/orders-processors-shipping-processor.json) | `ShipmentDispatchedHandler` (message) | `Shipment` + `ShipmentEventPublisher` |

How it works:

1. **The consumer test** describes an interaction (a request and the expected response, or the expected message) with matchers (`Match.Type`, `Match.Regex`) and runs the real consumer code against it. PactNet writes the pact to `pacts/*.json`.
2. **The provider test** replays the pact against the real provider code: it sends requests to the API (with "Given ..." state setup) or asks for a message built by the domain code.

How this differs from the schema snapshots in `ShopFlow.Contracts.Tests`: a snapshot protects the whole contract against any change. A pact records only what a specific consumer uses. The provider can add and change fields nobody reads, while removing or changing the type of a field a consumer relies on stops the provider's build.

Order in CI: consumer tests first (they regenerate the files), then providers. Both kinds are in one xUnit collection without parallelism. A changed pact file goes into the pull request together with the consumer change, so code review sees the contract change. The pact files are committed: never add `pacts/` to `.gitignore`.

Notes:

- `PACT_DO_NOT_TRACK=true` (set when the test project starts) stops the Pact library from sending anonymous statistics.
- The message provider works through a custom HTTP endpoint (`MessageProviderHost`), because the one built into PactNet 5.0.x has a bug with the `message://` transport (pact-net #530, #544).
- A Pact Broker is not needed in a monorepo. With separate repositories pacts are published to a broker, and a deployment asks `can-i-deploy`.
