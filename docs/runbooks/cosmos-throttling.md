# Runbook: Cosmos DB rejects requests (429)

Alert: `cosmos-429` (more than 500 responses with 429 within 15 minutes in one container).

## Symptoms and impact

A 429 response means the operation exceeded the throughput (RU/s) of the database or a physical partition. The SDK
retries it by itself, waiting as long as the `x-ms-retry-after-ms` header says (up to 9 times and 30 s in total,
`CosmosOptions`). Single 429s in the metrics are therefore normal. The problem starts when the retries run out:

| Where | What the customer sees |
|---|---|
| `orders-api` | a slow `POST /orders`, then 500 (`internal_error`; a 429 `CosmosException` has no dedicated mapping in `ShopFlowExceptionHandler`) |
| `readmodels-api` | a slow order list |
| processors (Change Feed, queues) | growing outbox and projection lag; messages in the DLQ after the attempts are exhausted |

## Diagnosis

### 1. Which container and which operation

[cosmos-ru.kql](../kql/cosmos-ru.kql), section "429s per container and operation" (the `CDBDataPlaneRequests` table).
Look at `OperationName` and `RequestCharge`: an expensive query (hundreds of RU) is a different problem from many cheap writes.

### 2. The whole database or one partition

Throughput is shared at the database level (autoscale, one maximum per database in prod), but the limit also applies per
physical partition.

- **The `NormalizedRUConsumption` metric** (portal → Cosmos account → Metrics, split by `PartitionKeyRangeId`):
  100% on one partition with the others low = **a hot partition**.
- [cosmos-ru.kql](../kql/cosmos-ru.kql), section "hot partition keys" (the `CDBPartitionKeyRUConsumption` table):
  which key consumes the most.

### 3. Who generates the traffic

[cosmos-ru.kql](../kql/cosmos-ru.kql), section "RU per application operation" (the `shopflow.cosmos.request_charge`
metric with the `db.cosmosdb.container` and `db.operation.name` dimensions), to point at a handler or an endpoint.

## Actions

| Cause | Immediate action | Permanent fix |
|---|---|---|
| Overall traffic above the autoscale limit (prod, test) | raise the database's autoscale maximum (below) | `maxAutoscaleThroughput` in `.bicepparam` through a pull request |
| A spike in consumer parallelism (e.g. after a DLQ replay or a backlog) | lower the functions' `maximumInstanceCount` or the container's `maxReplicas` | a scale cap matched to the database's RU |
| An expensive query (cross-partition, missing index) | the feature's flag, if one exists; roll back the deployment that introduced it | fix the query or the indexing policy |
| A hot partition | limit the traffic source (e.g. one B2B customer, a bot in the catalog: a rate limit in APIM) | changing the partition key is a new container and a migration ([ADR-0007](../adr/0007-cosmos-partition-keys.md)) |
| dev (serverless) | serverless has no RU setting; the limit is per partition | performance tests only in test |

Raising the database's autoscale maximum (immediately, without a deployment; record it in the ticket):

```bash
COSMOS=cosmos-shopflow-$ENV-weu
az cosmosdb sql database throughput show -g $RG_DATA -a $COSMOS -n orders --query resource.autoscaleSettings
az cosmosdb sql database throughput update -g $RG_DATA -a $COSMOS -n orders --max-throughput 20000
```

Cost: autoscale bills hourly for the highest value reached, never less than 10% of the maximum. Raising the maximum
from 10,000 to 20,000 RU/s also doubles the database's minimum cost ([cost.md](../cost.md)).

**Do not:** switch the account's consistency or the connection mode during an incident; do not increase
`MaxRetryAttemptsOnRateLimitedRequests` (longer retries tie up threads and lengthen API response times).

## Verification

- The number of 429s in [cosmos-ru.kql](../kql/cosmos-ru.kql) drops below the alert threshold and the alert resolves.
- `outbox-lag` and `projection-lag` return below their thresholds.
- Messages that reached the DLQ with `MaxDeliveryCountExceeded` in the meantime: [dlq-growth.md](dlq-growth.md).

## Follow-up

- A manual throughput change goes back into `infra/parameters/{env}.bicepparam`; otherwise the next infrastructure
  deployment reverts it.
- If a replay or a backlog was the cause, consider a permanently lower `maximumInstanceCount` for the consumer.
