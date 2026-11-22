# Runbook: payment failure and a saga waiting for an operator

Two situations that usually go together: (A) the payment service provider (PSP) is down or rejects our requests,
(B) the saga could not undo something and waits for a human decision.

---

## A. The payment provider does not respond or rejects requests

Alert: `payment-provider-errors` (more than 20% failed HTTP calls from `payments-processor` within 10 minutes).

### Symptoms and impact

| Step | What happens | Customer |
|---|---|---|
| `AuthorizePayment` | the handler throws a transient exception, the message goes back to the queue; after 5 attempts DLQ | after 15 min (`PaymentTimeout`) the saga rejects the order with `payment_timeout` |
| `CapturePayment` | as above | after 5 min the saga refunds (`RefundPayment`) and rejects the order |
| compensations (`VoidPayment`, `RefundPayment`) | wait in the queue for the PSP to come back | the hold on the card lasts longer; if the outage drags on the saga escalates (section B) |
| 3-D Secure webhook | the PSP retries the webhook on its side | the 3DS result may arrive after the saga's timeout |

The resilience policy (`AddPaymentProviderResilience`): 15 s per attempt, 45 s in total, 2 retries (POST only with an
idempotency key), the circuit opens for 30 s when half of at least 10 calls within 60 s fail.

### Diagnosis

1. **PSP status**: the provider's status page, the partner channel.
2. **Response codes** from [payments.kql](../kql/payments.kql), section "PSP calls":

| Code | Meaning | Action |
|---|---|---|
| timeout, 5xx, `BrokenCircuitException` | an outage at the PSP or in the network | wait; check the NAT Gateway and the IP allowlist at the PSP if only we are affected |
| 401, 403 | the API key is invalid (e.g. after a rotation) or our IP address was removed from the PSP's list | [secret-rotation.md](secret-rotation.md): is `psp-api-key` current and did the application refresh it |
| 400, 422 | the PSP rejects the request (format, payment method token) | a permanent error, the message goes straight to the DLQ; a code bug or an API change at the PSP |
| 409 | the same idempotency key with different parameters | a bug in our code; do not retry |

3. **Outbound address**: every call leaves from the NAT Gateway's public IP (`pip-shopflow-nat-{env}-weu`).
   Changing that address (e.g. recreating the NAT Gateway) requires updating the list at the PSP.

### Actions

- **An outage shorter than a few minutes:** nothing. Message retries and saga timeouts take care of the orders.
- **A longer outage:** orders placed during that time will be rejected after 15 minutes. Inform customer support and put
  a notice in the shop. The repository has no "pause checkout" flag; that is a reported gap (see below).
- **A fraud wave** at the PSP: the `Payments.ForceThreeDSecure` feature flag in App Configuration forces 3-D Secure for
  all payments (change the flag, then change the `Shared:Sentinel` value so that the applications reload it).
- **After the PSP is back:** replay the messages from the `payments-commands` DLQ according to [dlq-growth.md](dlq-growth.md),
  **compensations before commands**. A late `AuthorizePayment` for an already rejected order is safe: `VoidPayment` for
  a non-existent payment leaves a "tombstone", and an authorization that hits a tombstone does not charge the card.

### Verification

The PSP failure rate in [payments.kql](../kql/payments.kql) drops back to zero, the `payments-commands` queue is empty,
and the saga funnel ([saga-funnel.kql](../kql/saga-funnel.kql)) shows `Confirmed` again.

---

## B. Saga in the RequiresManualIntervention state

Alert: `saga-manual-intervention` (a Critical entry "Order saga order-… for order … requires manual intervention: …").

### When it happens

- a compensation (`ReleaseStock`, `VoidPayment`, `RefundPayment`, `CancelShipment`) did not get a success reply within
  10 minutes (`CompensationTimeout`),
- a call to `orders-api` (`ConfirmOrder`, `RejectOrder`, `CompleteOrderCancellation`) failed after 8 attempts spread over
  a dozen or so minutes.

The instance waits without a time limit and costs nothing. The order is stuck in an intermediate state, and the
customer's money may be on hold or captured.

### Diagnosis

The operator API lives in the `order-saga` function app, reachable only from the VNet (private endpoint), with the
`Saga.Operator` role. `az rest` obtains a token for the given resource, so signing in with an account that has the role is enough:

```bash
ORDER_ID=<from the alert text>
SAGA_URL=https://func-shopflow-order-saga-$ENV-weu.azurewebsites.net/api/sagas/$ORDER_ID
az rest --method get --url "$SAGA_URL" --resource api://shopflow-func-order-saga
```

The response contains the status, the current step (e.g. `compensation failed: RefundPayment`) and the list of steps
with their outcomes (the same is in Cosmos `saga/saga-state` and in the Durable Task Scheduler dashboard). The cause on
the participant's side is shown by [order-trace.kql](../kql/order-trace.kql) for the order's `CorrelationId`.

### Decision

| Cause | Action | `action` |
|---|---|---|
| The participant was unavailable and works now (the PSP is back, `orders-api` after a deployment) | retry | `retry` |
| The compensation command is in the DLQ | remove the cause first; then `retry` (the saga sends a new command with the next attempt number; do not replay the old one from the DLQ) | `retry` |
| The compensation cannot be done automatically (e.g. the PSP rejects the refund for this transaction) | do it manually: a refund in the PSP panel, cancelling the shipment at the carrier, a stock correction through back-office; record the identifiers in the note | `markResolved` |
| `orders-api` rejects the confirmation because back-office closed the order | check the order state; undo the side effects manually | `markResolved` |

```bash
az rest --method post --url "$SAGA_URL/resolution" --resource api://shopflow-func-order-saga \
  --body '{"action":"retry","note":"PSP back at 14:05, INC-1234"}'
```

A `202` response means the event reached the instance; `409` means the saga has already finished. The saga takes the
operator's name from the token (Easy Auth), and the note goes into the step history.

### Verification

`GET $SAGA_URL` shows `Rejected`, `Cancelled` or `Confirmed`. If after `retry` the saga goes back to
`RequiresManualIntervention`, the cause has not been removed: do not retry in a loop.

---

## Follow-up

- A list of orders rejected with `payment_timeout` during the outage ([saga-funnel.kql](../kql/saga-funnel.kql)) for
  customer support.
- Every `markResolved` has the identifier of the manual operation (refund, cancellation) in the ticket.
- A reported gap: no flag to pause order intake during a long PSP outage (e.g. `Orders.CheckoutPaused` checked in
  `SubmitOrder`, returning 503 with `Retry-After`).
