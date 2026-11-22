---
name: trace-flow
description: Traces a business scenario, HTTP request or message through ShopFlow end to end - entry point, host, handler, store, outbox, relay, Service Bus entity, consumers, saga steps - and reports what executes where (deployable unit, Azure service, identity, file and line). Use for questions like "what happens when a customer places an order", "who consumes PaymentCaptured", or to write or check a walkthrough.
argument-hint: "<scenario | endpoint | message name>"
---

# Trace a flow through the system

The goal is a precise "what runs where" answer grounded in the code, in the format of
[docs/walkthroughs](../../../docs/walkthroughs/README.md). Check first whether a walkthrough already covers the
scenario; if so, verify it against the code instead of starting from scratch.

## Find the entry point

| Starting from | Search |
|---|---|
| HTTP endpoint | `MapGet`, `MapPost`, `MapPut`, `MapDelete` in `src/Services/**/Endpoints/**`, `*Endpoints.cs`; route prefix in APIM policies |
| Function | `[Function("<name>")]` in `src/Services/**/*.Functions/**`, `*.Processors/**`, `*.Projections/**` |
| Message | the contract `[MessageContract("<Name>", n)]`, then `AddHandler<...<Name>` registrations and outbox writes |
| Saga step | `OrderSagaWorkflow.cs` (`ExecuteStepAsync`), `SagaReplyMapper.cs`, `SagaMessageHandlers.cs` |
| Background work | `BackgroundService` implementations (`ServiceBusQueueConsumer`, `SqlOutboxPollerService`) in Worker hosts |

## Follow the hops

For every hop record:

1. **Where it executes:** deployable unit (e.g. `orders-api` on App Service, `inventory-worker` on Container Apps,
   `order-saga` on Functions Flex), from `docs/architecture/00-overview.md` and the `Program.cs` of the host.
2. **Code:** class and method with a relative file link and line number.
3. **State:** which store and partition/table it reads or writes, and whether the write includes an outbox record.
4. **Message out:** contract and version, entity from `messaging-topology.json` (topic → subscription → forwardTo
   queue), session id if `IOrderScoped`.
5. **Identity and permission:** the managed identity of the unit and the role that allows the hop
   (`docs/architecture/identity-rbac.md`).
6. **Failure behavior:** retries, DLQ, compensation, timeout, idempotency guard at this hop.

Stop at the end of the business outcome (confirmation, notification sent, read model updated) or when the flow
leaves the system (PSP, carrier, ACS).

## Output

A numbered table `| # | Where (unit / Azure service) | What happens | Code | Data / message |`, a Mermaid
`sequenceDiagram` when there are more than four hops, and a "Failure variants" list. If the trace contradicts an
existing walkthrough or diagram, list the discrepancy with both file references.
