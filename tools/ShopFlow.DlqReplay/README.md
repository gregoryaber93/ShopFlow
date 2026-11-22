# shopflow-dlq

Peeks at and replays messages from a Service Bus dead-letter queue. Used in the [DLQ growth](../../docs/runbooks/dlq-growth.md) runbook.

```powershell
dotnet run --project tools/ShopFlow.DlqReplay -- peek --namespace sbns-shopflow-prod-weu.servicebus.windows.net --queue notifications-inbox
dotnet run --project tools/ShopFlow.DlqReplay -- replay --queue notifications-inbox --reason MaxDeliveryCountExceeded --dry-run
dotnet run --project tools/ShopFlow.DlqReplay -- replay --topic orders-events --subscription saga-start --message-id 0193...
```

> The commands need a real namespace and an `az login` session. In this learning repository they are documentation only.

Rules built into the tool ([ReplayPlanning.cs](ReplayPlanning.cs)):

| Rule | Why |
|---|---|
| `MessageId` unchanged | The consumer's inbox recognizes the repeat if the first processing partly succeeded. |
| Skips messages younger than the duplicate detection window (10 min) | The broker would silently drop a copy with the same `MessageId`, and the send would still report success. |
| At most 3 replays (`ReplayCount`) | A message that keeps coming back to the DLQ needs a code or data fix, not another attempt. |
| Send first, then remove from the DLQ | A failure half-way produces a duplicate (handled by the inbox), not a lost message. |
| `--dry-run` and the `--message-id`, `--reason` filters | We replay deliberately chosen messages after removing the cause, not the whole queue blindly. |

Where a replayed message goes follows from the [topology](../../infra/modules/messaging/messaging-topology.json) embedded in the tool:

- Queue DLQ: back to the same queue.
- DLQ of a subscription with auto-forward: to the target queue (the message got stuck on the forward).
- DLQ of a subscription read directly (`saga-start`, `invoicing`, `orders`): to the topic with `ReplayTarget = <subscription>`. A message cannot be sent to a subscription directly, and the rules in Bicep make sure only the named subscription receives it.

Access: the namespace has no public endpoint, so the tool runs from the VNet (Azure Bastion or a Container Apps Job). The operator signs in with `az login` and activates the Data Receiver and Data Sender roles temporarily through Privileged Identity Management.

Walkthrough: [05-poison-message-and-replay.md](../../docs/walkthroughs/05-poison-message-and-replay.md).
