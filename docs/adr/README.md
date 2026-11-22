# Architecture Decision Records

Each ADR records one decision: the context, the choice, the rejected options and the consequences, including the
uncomfortable ones. An ADR is not edited after the fact. Changing a decision means a new ADR with the status
"Supersedes ADR-00xx", and the old one gets the status "Superseded by ADR-00yy".

| No. | Decision | Status |
|---|---|---|
| [0001](0001-monorepo.md) | Monorepo with independently deployed services | Accepted |
| [0002](0002-bicep-over-terraform.md) | Bicep instead of Terraform | Accepted |
| [0003](0003-compute-per-workload.md) | App Service, Container Apps or Functions per workload | Accepted |
| [0004](0004-functions-flex-consumption-and-dts.md) | Functions Flex Consumption and Durable Task Scheduler | Accepted |
| [0005](0005-service-bus-event-hubs-event-grid.md) | Service Bus, Event Hubs or Event Grid | Accepted |
| [0006](0006-cosmos-or-sql-per-context.md) | Cosmos DB or Azure SQL per context | Accepted |
| [0007](0007-cosmos-partition-keys.md) | Partition keys in Cosmos DB | Accepted |
| [0008](0008-saga-orchestration-vs-choreography.md) | Saga: the boundary between orchestration and choreography | Accepted |
| [0009](0009-outbox-and-inbox.md) | Two outbox variants and an inbox | Accepted |
| [0010](0010-managed-identities-and-app-roles.md) | Managed identities and app roles | Accepted |
| [0011](0011-network-topology.md) | Network topology and exceptions to private endpoints | Accepted |
| [0012](0012-contract-versioning.md) | Versioning of message contracts and APIs | Accepted |
| [0013](0013-single-application-insights.md) | One Application Insights per environment | Accepted |
| [0014](0014-deployment-strategies.md) | Deployment strategies: slots, revisions, rolling update | Accepted |
| [0015](0015-environment-parity-and-cost.md) | Environment parity and cost levers | Accepted |
| [0016](0016-redis-scope-and-isolation.md) | Redis: scope of use and isolation | Accepted |

## Template

```markdown
# ADR-00NN: Title

| Status | Date | Deciders |
|---|---|---|
| Proposed / Accepted / Superseded by ADR-00xx | YYYY-MM-DD | teams |

## Context
What problem, which forces (requirements, constraints, costs).

## Decision
What we choose, in one paragraph, in the indicative mood.

## Options considered
Table: option, pros, cons.

## Consequences
What we gain, what we lose, what to watch.

## In the repository
Where the decision is visible in the code.
```

To write a new ADR with Claude Code, use the `/write-adr` skill (`.claude/skills/write-adr`).
