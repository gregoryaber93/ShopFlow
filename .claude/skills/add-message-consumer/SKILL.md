---
name: add-message-consumer
description: Adds a consumer for a ShopFlow message - an idempotent handler, its registration, the Service Bus subscription or queue in the topology, the trigger in the host, the receiver role, tests for duplicates and ordering, and docs. Use when a context must react to another context's event or handle a new command.
argument-hint: "<consuming context> <ContractName vN>"
---

# Add a message consumer

Read `.claude/rules/messaging.md` and [message-flows.md](../../../docs/architecture/message-flows.md) first.

## 1. Choose the entity

| Message | Entity | Pattern |
|---|---|---|
| Command to this context | the context's command queue (`<ctx>-commands`) | point-to-point |
| Event from another context, ordered per order | subscription on `<publisher>-events` with auto-forward to the context's session-enabled inbox queue | publish-subscribe + session |
| Event read directly (e.g. `saga-start`) | subscription read by the function itself | publish-subscribe |

Look at how existing consumers are declared in `infra/modules/messaging/messaging-topology.json` and copy the closest
one. Add the `Subject` filter (`sys.Label` / contract name) so only the needed messages are delivered.

## 2. Topology and permissions

- JSON (`serviceBus.topics[].subscriptions[]`): `name`, `filter` (`{ "correlation": { "subject": "<Message>" } }`
  or `{ "subjects": [ ... ] }`), and either `forwardTo: "<inbox queue>"` or `consumer: "<deployable unit>"` for a
  directly read subscription. The target queue's `messages` list must contain the message.
- `src/BuildingBlocks/ShopFlow.Contracts/MessagingTopology.cs`: constants for new names.
- Roles follow from the JSON in `servicebus.bicep` (Data Receiver on the queue/subscription for the consumer's
  identity). A KEDA-scaled Container Apps consumer also needs the queue in the metrics-reader list.
- `dotnet test --project tests/ShopFlow.Contracts.Tests` and `--project tests/ShopFlow.Architecture.Tests`
  (`MessagingTopologyTests`, `RoutingTopologyTests`) must pass.

## 3. The handler (Application layer)

```csharp
public sealed class <Name>Handler(<ports...>) : IMessageHandler<Contracts.<Ctx>.V1.<Message>>
{
    public async Task HandleAsync(<Message> message, MessageContext context, CancellationToken cancellationToken)
    {
        // 1. load state by id (point read)            2. idempotency: already applied? return
        // 3. domain operation (Result<T>)             4. outbox: follow-up messages in the same unit of work
    }
}
```

- Idempotent by design: inbox (`IInbox`) for side effects, or natural idempotency (state already reached, deterministic
  ids, ETags). The same message twice must give the same result and no second side effect.
- Missing prerequisite state → `OutOfOrderMessageException` (retried). Invalid message → a permanent failure (DLQ).
- No Azure SDK types here; ports only.

## 4. Wire the host

- `Program.cs` of the deployable: `.AddHandler<V1.<Message>, <Name>Handler>()` on the context's messaging builder.
- Functions host: a `[ServiceBusTrigger(..., AutoCompleteMessages = false)]` method that calls
  `adapter.ProcessAsync(message, actions, Endpoint, cancellationToken)` (copy `ShippingMessagingFunctions`);
  session-enabled queues set `IsSessionsEnabled = true`. Worker host: the processor registration in `Program.cs`.
- `host.json`: concurrency (`maxConcurrentCalls` / `maxConcurrentSessions`) sized for the downstream dependency.

## 5. Tests and docs

- Unit tests: happy path, **the same message twice**, message before its prerequisite, permanent failure.
- Docs: `message-flows.md` tables, the service README function table, the relevant walkthrough, and an alert/runbook
  entry if the consumer can block a business flow.
- Finish with `/verify` and the `architecture-reviewer` agent.
