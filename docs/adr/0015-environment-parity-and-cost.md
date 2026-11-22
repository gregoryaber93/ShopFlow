# ADR-0015: Environment parity and cost levers

| Status | Date | Deciders |
|---|---|---|
| Accepted | 2026-11-14 | platform, finance |

## Context

The most expensive production failures in Azure come from differences between environments: a different network path (a
public endpoint in dev, a private endpoint in prod), a different service tier with different features (Service Bus
Standard without partitioned sessions, APIM Developer without VNet integration), a different identity model. A full copy
of prod in dev, however, costs as much as prod.

## Decision

**Same shape, different size.** dev, test and prod have the same network topology, the same service tiers wherever the
tier changes features (Service Bus Premium, APIM Standard v2, Front Door Premium), and the same identity model. They
differ in size, redundancy, retention and instance counts. The differences are parameters in `.bicepparam`, not
conditions scattered across modules.

| Lever | dev | prod |
|---|---|---|
| Cosmos DB | serverless | autoscale up to 10,000 RU/s, zone redundancy, continuous backup |
| Azure SQL | GP serverless 2 vCore, auto-pause after 60 min | GP 4 vCore provisioned, zone redundancy |
| App Service | P0v3 × 1 | P1v3 × 3–10 (zones) |
| Functions | always-ready 0, max 20 | always-ready 1, max 100 |
| Managed Redis | Balanced B0 without HA | Balanced B5 with HA |
| Event Hubs | 1 TU | 2 TU + auto-inflate up to 10 |
| Log Analytics | 30 days, 5 GB/day cap, 100% sampling | 90 days, no cap, 25% sampling |

## Options considered

| Option | Pros | Cons |
|---|---|---|
| **Same shape, different size** (chosen) | network and identity bugs surface in dev; one set of modules | a fixed dev cost of about 2–2.5k USD/month ([cost.md](../cost.md)) |
| A cheaper dev (Service Bus Standard, APIM Developer, no private endpoints) | about 1.5k USD/month less | a different network path and different features; conditions in Bicep; bugs only in test |
| Ephemeral environments per pull request | isolation of changes | creation time of APIM and Front Door (tens of minutes) and a fixed cost for every copy |
| Shutting dev down at night | compute savings | the largest fixed costs (Service Bus Premium, APIM, Front Door) have no "pause" mode |

## Consequences

- **Fixed cost dominates in dev**: Service Bus Premium, APIM Standard v2, Front Door Premium and private endpoints cost
  the same with zero traffic. That is the price of parity, quantified in [cost.md](../cost.md).
- **A budget per environment** with 80% and 100% alerts (bootstrap) filters costs by the `environment` tag.
- **To be revisited** if dev does not catch bugs that test would not catch: then dev moves to cheaper tiers (decision D3
  in the overview).
- Test has intermediate sizes and the `notify-test` domain, so performance and e-mail tests do not touch prod.

## In the repository

[infra/parameters](../../infra/parameters), [types.bicep](../../infra/shared/types.bicep),
the budget in [infra/bootstrap/environment.bicep](../../infra/bootstrap/environment.bicep), [cost.md](../cost.md).
