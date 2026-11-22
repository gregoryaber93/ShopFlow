# Cost

An estimate of the monthly cost of the three environments, what drives it and what the levers are. The environment
parity decision: [ADR-0015](adr/0015-environment-parity-and-cost.md).

> **The amounts are indicative:** USD, West Europe, pay-as-you-go list prices without discounts (reservations, savings
> plan, Azure Hybrid Benefit, an EA agreement). Azure prices change; check them in the Azure Pricing Calculator before
> making a decision. The proportions and which items are fixed matter more than the amounts.
>
> This repository is for learning: it deploys nothing by itself, and local builds and tests cost nothing.

## Summary

| Environment | Monthly estimate | Budget (bootstrap) | What dominates |
|---|---|---|---|
| dev | about 2.2–2.6k | 2,500 | fixed costs: Service Bus Premium, APIM, Front Door, private endpoints |
| test | about 3–3.5k | 4,000 | like dev + Cosmos autoscale, a larger Redis |
| prod | about 10–14k | 15,000 | Service Bus 2 MU, APIM 2 units, Cosmos, SQL, Log Analytics |

Budgets with 80% and 100% alerts are created by [infra/bootstrap/environment.bicep](../infra/bootstrap/environment.bicep),
filtered by the `workload=shopflow` and `environment` tags.

## Fixed versus variable cost

In dev traffic is close to zero, so the bill is almost entirely **fixed cost**: resources that cost the same regardless
of the number of orders. That is the price of the same network shape and the same tiers as in prod.

| Fixed item (dev) | About USD/month | Why it exists |
|---|---|---|
| Service Bus Premium, 1 MU | 680 | private endpoints, resource isolation, sessions in large queues; Standard has no private endpoints |
| API Management Standard v2, 1 unit | 700 | VNet integration; Developer has no SLA, Basic v2 has no VNet integration |
| Front Door Premium | 330 + traffic | Private Link to APIM and Storage, WAF managed rules |
| Private endpoints (about 25) | 180 + processed data | data without public endpoints |
| Event Hubs Standard 1 TU + Capture | 95 | Capture is billed per TU, regardless of volume |
| App Service P0v3 × 1 | 60 | the `staging` slot (Basic does not have it) |
| Managed Redis Balanced B0 | 45 | a cache with Entra ID and a private endpoint |
| NAT Gateway + public IP | 40 + processed data | a fixed address for the PSP and the carrier |
| Private DNS zones (13), App Configuration, Key Vault, alerts | 30 | |
| **Total fixed** | **about 2.2k** | |

Variable costs in dev (usually tens of dollars): Cosmos serverless (for RU consumed), SQL serverless (for running time
before auto-pause), Functions Flex (for executions), Container Apps Consumption, Log Analytics (up to the 5 GB/day cap).

## Items per environment

| Service | dev | test | prod | What drives the cost |
|---|---|---|---|---|
| Front Door Premium + WAF | 330 | 330 | 600–1,200 | base fee, requests, outbound traffic |
| API Management Standard v2 | 700 | 700 | 1,400 | units (2 in prod); above the request allowance included in the unit price, a per-request charge |
| App Service (`orders-api`) | 60 | 60 | 370–1,250 | P1v3 × 3–10 in prod (zones require 3) |
| Container Apps (3 apps + job) | 20–40 | 40–80 | 150–400 | vCPU-seconds and GiB-seconds of replicas; minimum replicas in prod |
| Functions Flex × 7 | 0–10 | 10–30 | 250–450 | executions and GB-seconds; always-ready (`http` group) in 5 apps in prod |
| Durable Task Scheduler | 5–20 | 5–20 | a few hundred (Dedicated) | Consumption per action; Dedicated per capacity unit |
| Service Bus Premium | 680 | 680 | 1,350 | messaging units (MU), not the number of messages |
| Event Hubs Standard + Capture | 95 | 95 | 190–950 | TUs (auto-inflate up to 10) and Capture per TU |
| Cosmos DB | 5–20 | 300–600 | 1,000–2,500 | serverless in dev; autoscale per database (min. 10% of the maximum), ×1.25 for zones, continuous backup |
| Azure SQL | 50–130 | 150–300 | 1,500–1,800 | GP serverless with auto-pause in dev; 4 vCore provisioned with zones in prod |
| Managed Redis | 45 | 90 | 300–400 | size and HA (B5 with a replica in prod) |
| Storage × 3 | 5 | 10 | 50–150 | invoices grow over time (lifecycle moves them to cool/cold/archive), Capture in the lake |
| Log Analytics + Application Insights | 50–400 | 100–400 | 1,000–2,000 | **data volume** (GB/day), retention beyond the included period |
| Private endpoints, DNS, NAT Gateway | 230 | 230 | 260 + data | number of endpoints, GB processed |
| Communication Services | 0–5 | 5–20 | 100–500 | number of e-mails and SMS (SMS dominates) |
| Container Registry Premium (shared) | 50 in total | | | one for all environments |

Outside Azure: GitHub-hosted runners with Azure private networking are billed as larger runners (per minute) and require
the GitHub Enterprise Cloud plan.

## Observability

Telemetry is the item that most often surprises: you pay for every GB ingested into Log Analytics, and the volume grows
with traffic and with how chatty the code is.

| Lever | Where | Setting |
|---|---|---|
| Trace sampling | `Observability:SamplingRatio`, `samplingPercentage` in `.bicepparam` | 100% in dev and test, 25% in prod; metrics not sampled ([ADR-0013](adr/0013-single-application-insights.md)) |
| Daily cap | `dailyQuotaGb` | 5 GB in dev; none in prod (losing telemetry during an incident costs more) |
| Retention | `retentionDays` | 30 days in dev and test, 90 in prod; longer is archive, not interactive retention |
| Log levels | `Logging:LogLevel` in appsettings.json, `logging.logLevel` in the functions' host.json | `Information` by default, `Warning` for chatty libraries (`Microsoft.AspNetCore`, `Azure.Core`, EF Core commands, `DurableTask.Core`) |
| Resource diagnostic logs | diagnostic settings in the Bicep modules | only the categories needed for queries and alerts |
| Table plans | `Basic`/`Auxiliary` tables for logs that are not queried day to day | not used in the project, a candidate in prod |
| Commitment tier | the prod workspace | cheaper than pay-as-you-go from about 100 GB/day |

Which tables consume the most:

```kusto
Usage
| where TimeGenerated > ago(30d) and IsBillable == true
| summarize GB = round(sum(Quantity) / 1024, 1) by DataType
| order by GB desc
```

VNet flow logs are not enabled: with more than a dozen private endpoints and application traffic they are extra GB per
day, and network diagnostics are covered here by service metrics and logs.

## Cosmos DB: how RU are billed

- **Serverless (dev):** you pay for the RU consumed and storage; no minimum, but a throughput limit per partition.
- **Autoscale (test, prod):** you pay hourly for the highest RU/s reached in that hour, never less than 10% of the
  configured maximum. Throughput is shared at the database level (7 databases), so the minimum cost in prod is
  7 × 1,000 RU/s × the autoscale rate × 1.25 for zone redundancy (about 750 USD/month) with zero traffic.
- Raising the maximum during an incident ([cosmos-throttling.md](runbooks/cosmos-throttling.md)) raises that minimum too.
- The cheapest RU are the ones not spent: single-partition queries, point reads (`ReadItem`, 1 RU per 1 KB) instead of
  queries, indexing only the fields used in filters. What costs the most: [cosmos-ru.kql](kql/cosmos-ru.kql).

## Levers and their price

| Lever | Savings (order of magnitude) | What we lose |
|---|---|---|
| dev on Service Bus Standard and APIM Developer (decision D3) | about 1.3–1.5k/month | a different network path and different features than in prod; conditions in Bicep |
| Reservations and savings plan (App Service, SQL, Cosmos, Redis) | 20–40% of those items in prod | a 1- or 3-year commitment |
| Azure Hybrid Benefit for SQL | up to about 30% of SQL | requires SQL Server licences with Software Assurance |
| A lower `maxAutoscaleThroughput` in Cosmos | proportional to the 10% minimum | 429 risk during spikes |
| 10% sampling in prod | a few hundred USD/month | rare errors may lack a full trace |
| Shutting dev down at night | small | the largest items (Service Bus, APIM, Front Door) have no pause |

## Day-to-day control

- The mandatory tags (`workload`, `environment`, `service`, `costCenter`) let Cost Management split the cost by service
  and team; a missing tag is blocked by Azure Policy.
- A budget per environment (bootstrap) with notifications to the environment's alert address.
- A monthly review: [cosmos-ru.kql](kql/cosmos-ru.kql) (RU per operation), the `Usage` query above, the utilization of
  Service Bus and APIM units (are 2 units in prod needed).
