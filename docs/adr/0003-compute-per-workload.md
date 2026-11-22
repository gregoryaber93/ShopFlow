# ADR-0003: App Service, Container Apps or Functions per workload

| Status | Date | Deciders |
|---|---|---|
| Accepted | 2026-11-14 | architecture, platform |

## Context

The twelve deployable units have different profiles: steady HTTP traffic with a low-latency requirement, traffic spikes
in the catalog, queue consumers, long orchestrations, reactions to events with spiky traffic, one-off jobs (migrations).
One platform for everything would be simpler to run, but more expensive or worse for some workloads.

## Decision

The hosting is chosen by four questions: what triggers the work (HTTP, queue, event, schedule), how it should scale
(steadily, by requests, by queue length, to zero), how to deploy without downtime, and whether the process is long-running.

| Workload | Hosting | Main reason |
|---|---|---|
| `orders-api` | App Service Premium v3 | steady traffic, predictable latency, slots and swap |
| `inventory-api`, `readmodels-api` | Container Apps | scaling by concurrent requests within seconds; a container image |
| `inventory-worker` | Container Apps + KEDA | a long-running consumer with a SQL connection pool, scaled by queue length |
| `inventory-migrations` | Container Apps Job | a one-off job in the VNet, started by the pipeline |
| event processors, the saga | Functions Flex Consumption | event-driven work, scale to zero, triggers and Durable Functions ([ADR-0004](0004-functions-flex-consumption-and-dts.md)) |

## Options considered

| Option | Pros | Cons |
|---|---|---|
| **Platform per workload** (chosen) | every workload on the cheapest and best-fitting model | three platforms: three ways of deploying, diagnosing and hitting limits |
| Everything in Container Apps | one platform, KEDA for everything, Dapr | no slots (revisions are something else), Durable Functions in a container without the Flex trigger support; more hosting code |
| Everything in Functions | scale to zero everywhere | an API with predictable latency suffers from cold starts; no slots in Flex |
| AKS | full control | the operational cost of a cluster is out of proportion for 12 units |

## Consequences

- Three deployment actions in the pipeline: [deploy-appservice-slot](../../.github/actions/deploy-appservice-slot),
  [deploy-containerapp](../../.github/actions/deploy-containerapp), [deploy-functionapp](../../.github/actions/deploy-functionapp)
  ([ADR-0014](0014-deployment-strategies.md)).
- The same observability code in every host (OpenTelemetry, `ShopFlow.Observability`), so diagnostics do not depend on
  the platform.
- The same identity model (user-assigned, [ADR-0010](0010-managed-identities-and-app-roles.md)) and network model
  (VNet integration, private endpoints) on every platform.
- To watch: the cost of App Service in prod (a minimum of 3 instances with zone redundancy) versus Container Apps; with
  low traffic `orders-api` could move to Container Apps at the cost of slots.

## In the repository

[infra/layers/apps.bicep](../../infra/layers/apps.bicep), modules in [infra/modules/compute](../../infra/modules/compute),
the rationale table in [00-overview.md](../architecture/00-overview.md) (section 4).
