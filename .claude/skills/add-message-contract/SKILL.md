---
name: add-message-contract
description: Adds a new message contract (command or integration event) or a new version of an existing one to ShopFlow.Contracts, with the topology entry, schema snapshot, publisher via the outbox and docs. Use when the user wants a new message, a new field in a message, or a breaking change to a message.
argument-hint: "<ContractName> [command|event] [version]"
---

# Add or change a message contract

Read `.claude/rules/contracts.md` and [ADR-0012](../../../docs/adr/0012-contract-versioning.md) first.

## 1. Decide the kind of change

- **New field, optional:** add a nullable property to the existing version. Compatible. Go to step 4.
- **Anything else on an existing contract** (rename, remove, type change, required field, new enum value): create a
  new version `V<n+1>` side by side. Keep the old version untouched.
- **New message:** command (imperative name, sent to one queue) or event (past tense, published to the context's topic).

## 2. Write the contract

```csharp
namespace ShopFlow.Contracts.<Context>.V1;

/// <summary>One sentence: what happened / what to do, and who sends it.</summary>
[MessageContract("<Name>", 1)]
public sealed record <Name> : IIntegrationEvent, IOrderScoped   // or ICommand
{
    public required Guid OrderId { get; init; }
    // only BCL types and contract DTOs (MoneyDto, AddressDto, ...)
}
```

- `IOrderScoped` when the message belongs to one order: `OrderId` becomes the Service Bus `SessionId` (ordering).
- The `MessageTypeRegistry` discovers the type by reflection; no registration needed.
- New version: put it in `.V2`, and add an `IMessageUpcaster` from V1 to V2 when the old shape can be converted
  (see `OrderSubmittedV1ToV2Upcaster.cs`).

## 3. Route it (topology is data)

- Commands: add the message name to the target queue's `messages` in
  `infra/modules/messaging/messaging-topology.json` (`serviceBus.queues[]`); a new queue also gets its constant in
  `MessagingTopology.cs` (`Queues`). `senders` lists the deployable units allowed to send (Data Sender role).
- Events: add the message name to the publisher topic's `messages` (`serviceBus.topics[]`), then a subscription with a
  `Subject` filter for each consumer (`/add-message-consumer`). Bicep reads the JSON; do not edit `servicebus.bicep`
  for entity names.

## 4. Publish through the outbox

In the publishing context's application layer, add the message to `IOutbox` in the same unit of work as the state
change (e.g. `InventoryEventPublisher`, aggregate event mappers in Orders/Payments/Shipping). Never use a Service Bus
sender directly. During a version transition publish both versions behind a feature flag (App Configuration).

## 5. Tests

```bash
SHOPFLOW_ACCEPT_SNAPSHOTS=1 dotnet test --project tests/ShopFlow.Contracts.Tests
dotnet test --project tests/ShopFlow.Contracts.Tests
```

On Windows PowerShell: `$env:SHOPFLOW_ACCEPT_SNAPSHOTS = "1"; dotnet test --project tests/ShopFlow.Contracts.Tests; Remove-Item Env:SHOPFLOW_ACCEPT_SNAPSHOTS`.
Review the new `Snapshots/<Name>.v<n>.approved.json`. Add a unit test in the publisher's context that the message is
written to the outbox with the expected content, and an upcaster test for a new version.

## 6. Docs

Update `docs/architecture/message-flows.md` (publisher/subscriber tables), the walkthrough of the affected scenario,
and `docs/kql/messaging.kql` if a version sunset should be tracked. Then run the `contract-change-reviewer` agent and
`/verify`.
