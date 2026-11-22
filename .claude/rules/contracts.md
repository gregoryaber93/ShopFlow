---
paths:
  - "src/BuildingBlocks/ShopFlow.Contracts/**"
  - "tests/ShopFlow.Contracts.Tests/**"
  - "tests/ShopFlow.Pact.Tests/**"
  - "src/Services/**/Endpoints/**"
  - "infra/apim/**"
---

# Contract rules (messages and HTTP APIs)

Contracts are the only coupling between bounded contexts. A careless change breaks a consumer that is deployed
independently, possibly while old messages still sit in a queue or DLQ. See
[ADR-0012](../../docs/adr/0012-contract-versioning.md).

## Message contracts

- `public sealed record` with `required init` properties, in `ShopFlow.Contracts.<Context>.V<n>`.
- `[MessageContract("Name", version)]`; implements `ICommand` (imperative name: `ReserveStock`) or `IIntegrationEvent`
  (past tense: `StockReserved`); `IOrderScoped` when it belongs to one order (session id).
- Only BCL types and other contract DTOs; no domain types, no `object`, no polymorphism. Enums are serialized as
  camelCase strings; a new enum value is a breaking change for old consumers.
- **Within a version only additive changes**: a new optional (nullable) property. Renaming, removing, changing a type
  or making a field required means a **new version** (`V2`) next to the old one:
  1. add `V2` and, if possible, an `IMessageUpcaster` from V1 to V2;
  2. the publisher publishes both versions during the sunset period (behind a feature flag);
  3. consumers move to V2; telemetry (`docs/kql/messaging.kql`, query 4) shows when V1 is no longer processed;
  4. remove V1 publishing, later the V1 type.
- After a deliberate change: run `ShopFlow.Contracts.Tests` with `SHOPFLOW_ACCEPT_SNAPSHOTS=1`, review the
  `Snapshots/*.approved.json` diff and keep it in the same change. Never edit snapshot files by hand.

## Pact (consumer-driven contracts)

- Consumer tests generate `tests/ShopFlow.Pact.Tests/pacts/*.json`; provider tests replay them. Run consumers first.
- A changed pact file is committed with the consumer change; never edit or delete pact files by hand and never
  add `pacts/` to `.gitignore` (CI fails when the consumer tests change an uncommitted pact).
- Matchers describe only what the consumer reads (`Match.Type`, `Match.Regex`), not the whole response.

## HTTP APIs

- Public APIs are versioned in the path (`/v1`, `/v2`); a retired version answers with `Deprecation` and `Sunset`
  headers before it is removed. Errors are RFC 9457 problem details.
- Mutating endpoints accept `Idempotency-Key`; updates use `If-Match` with an ETag (optimistic concurrency).
- APIM policies (`infra/apim/policies/*.xml`) are part of the contract: rate limits, JWT validation, header rewrites.

Review contract changes with the `contract-change-reviewer` agent.
