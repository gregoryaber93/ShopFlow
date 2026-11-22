# ADR-0012: Versioning of message contracts and APIs

| Status | Date | Deciders |
|---|---|---|
| Accepted | 2026-11-14 | architecture |

## Context

Services deploy independently, and a function deployment is a rolling update, so the old and the new code version run
at the same time. Messages sit in queues and DLQs for hours, and a replay can bring back a message from a week ago.
Mobile clients update over months.

## Decision

**Messages:**

- Within a contract version only additive changes (a new optional field).
- A breaking change is a new type (`OrderSubmitted` in the `V2` namespace) published **side by side** with the old one
  during a sunset period. The version is in the `ContractVersion` property, which subscriptions filter on
  (`saga-start` reads only v2).
- Switching off the old version is a feature flag at the publisher (`Orders.PublishOrderSubmittedV1`), not a deployment.
- JSON schema snapshots (`*.approved.json`) in `ShopFlow.Contracts.Tests`: every schema change is in the diff and
  requires approval from the contract owners (CODEOWNERS).
- Pact (consumer → provider) for messages and for HTTP.

**HTTP APIs:**

- The version in the path (`/v1`, `/v2`), version sets in APIM.
- Older clients can receive a v2 response transformed into v1 by an APIM policy.

## Options considered

| Option | Pros | Cons |
|---|---|---|
| **A type per version + side-by-side publishing** (chosen) | the consumer decides when to move; no on-the-fly transformation | the publisher maintains two versions; more traffic during the transition |
| Schema registry (Avro, Event Hubs Schema Registry) | compatibility enforced at the broker | Service Bus does not support it; an extra dependency in the publishing path |
| "Tolerant reader" only | fewer versions | breaking changes happen anyway; no signal about who still reads the old field |
| Version in an HTTP header / `Accept` | clean URLs | harder caching in Front Door and APIM; less visible in logs |

## Consequences

- The review rule: adding an optional field is allowed within the same version; a required field, a removal or a type
  change means `V2` (the comment in [ContractSchemaTests.cs](../../tests/ShopFlow.Contracts.Tests/ContractSchemaTests.cs)).
- The sunset period needs observation: who still consumes v1 (spans with the `shopflow.message.contract_version`
  attribute, the query in [messaging.kql](../kql/messaging.kql)).
- Consumers outside the repository get the `ShopFlow.Contracts` package with API compatibility validation against the
  previous version (`EnablePackageValidation`).
- Documents in the database have their own schema version (`OrderDocument.SchemaVersion`) for the same reason as contracts.

## In the repository

[`ShopFlow.Contracts`](../../src/BuildingBlocks/ShopFlow.Contracts), [`tests/ShopFlow.Contracts.Tests`](../../tests/ShopFlow.Contracts.Tests),
[`tests/ShopFlow.Pact.Tests`](../../tests/ShopFlow.Pact.Tests), APIM policies in [infra/apim/policies](../../infra/apim/policies),
[contracts-publish.yml](../../.github/workflows/contracts-publish.yml). Procedure: the `/add-message-contract` skill.
