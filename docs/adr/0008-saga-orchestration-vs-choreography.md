# ADR-0008: Saga: the boundary between orchestration and choreography

| Status | Date | Deciders |
|---|---|---|
| Accepted | 2026-11-14 | architecture, team-checkout |

## Context

Placing an order touches four contexts (Orders, Inventory, Payments, Shipping) without a shared transaction. Part of the
process takes minutes and needs compensation (reservation, payment authorization and capture, creating a shipment).
Another part takes hours and days, and its steps do not undo each other (e-mail, invoice, delivery, projections,
analytics). Customer support must quickly answer "where is order X right now".

## Decision

**A hybrid.** The transactional part (from `OrderSubmitted` to confirmation or rejection) is **orchestrated** by Durable
Functions (`order-saga`). Everything after the confirmation is **choreography** on Service Bus topics: every context
reacts to events and nobody controls the whole.

## Options considered

| Option | Pros | Cons |
|---|---|---|
| **Orchestration of the transactional part, choreography of the aftermath** (chosen) | the process with compensations in one place in the code; timeouts as durable timers; aftermath steps can be added without touching the orchestrator | two styles to understand; the saga knows the participants' command contracts |
| Full choreography | no central point; loose coupling | compensations scattered across contexts; no single place with timeouts; the process state must be pieced together from logs |
| Full orchestration (including e-mail, invoice) | one place of truth | the orchestrator grows with every feature; instances live for days (until delivery); every change to the aftermath requires orchestration versioning |

| Orchestration engine | Pros | Cons |
|---|---|---|
| **Durable Functions** (chosen) | durable timers, external events, activity retries, the DTS dashboard; code in C# | determinism and versioning rules; dependency on the Functions host |
| A custom state machine in Cosmos | full control | timers, retries and concurrency written from scratch |
| Logic Apps | a visual flow | business logic outside the repo and unit tests |
| MassTransit saga | a mature pattern | a commercial licence (decision D7), hides the Azure SDKs this project is meant to teach |

## Consequences

- **Visibility:** the instance status (custom status), the `saga-state` projection and `GET /api/sagas/{orderId}` answer
  "where is the order" without piecing logs together.
- **Coupling:** the saga knows the participants' command contracts and their replies. Changing a command contract
  requires a change in the saga (Pact enforces compatibility).
- **State-tolerant compensations** are a requirement on the participants, not an implementation detail of the saga
  ([saga.md](../architecture/saga.md#compensation-rules)).
- **The manual path:** when a compensation fails, the saga waits for an operator (`RequiresManualIntervention`) instead
  of guessing ([payment-failure.md](../runbooks/payment-failure.md)).
- Participant replies are ordinary events on their topics (a `saga` subscription forwarding to `saga-replies`), so Read
  Models and Analytics see the same facts.

## In the repository

[OrderSagaWorkflow.cs](../../src/Services/OrderSaga/ShopFlow.OrderSaga/Workflow/OrderSagaWorkflow.cs),
[saga.md](../architecture/saga.md), [message-flows.md](../architecture/message-flows.md#aftermath-of-the-confirmation-choreography).
