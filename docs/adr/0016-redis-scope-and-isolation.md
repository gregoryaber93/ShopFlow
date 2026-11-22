# ADR-0016: Redis: scope of use and isolation

| Status | Date | Deciders |
|---|---|---|
| Accepted | 2026-11-14 | architecture |

## Context

Three places need a cache: product cards and category lists (the catalog, read-dominated), prices during checkout
(protection against `inventory-api` being unavailable) and order details in the read model. A distributed cache is
needed because the APIs have many replicas. A separate Redis instance per service is a separate fixed cost and a
separate private endpoint.

## Decision

- **One Azure Managed Redis instance per environment**, accessed through Entra ID (keys disabled), private endpoint.
- **Isolation through key prefixes**, added automatically by `ShopFlow.Azure` (`inventory:`, `orders:`, `rm:`).
- **Redis only as a cache**, never as a store of truth, a queue or a distributed lock. Every read has a path without
  Redis (the database or the source service).
- **`HybridCache`** (L1 in process memory + L2 in Redis) where invalidation comes from the same process; no L1 for the
  read model, because the projector invalidates it from another process.

## Options considered

| Option | Pros | Cons |
|---|---|---|
| **A shared instance, prefixes** (chosen) | one fixed cost, one private endpoint | no hard isolation: every identity with the `default` policy sees all keys; "noisy neighbour" |
| An instance per service | hard isolation and independent scaling | cost ×3 and three private endpoints per environment |
| Logical databases (`SELECT n`) | namespace isolation | Managed Redis in cluster mode has a single database |
| Access policies restricted to a key pattern | isolation in Redis ACLs | custom policies in Managed Redis; more configuration per identity |
| An in-process cache only | zero infrastructure | every replica has its own copy; no invalidation across processes |

## Consequences

- **A deliberate security trade-off:** a compromised `readmodels-api` identity could read `inventory:*`. The data in the
  cache is public (the catalog) or available to that service anyway; payment data never goes into Redis.
- **A Redis outage degrades performance, not availability:** `CatalogPricingClient` with an open circuit uses the last
  known price from the cache, but with both Redis and the catalog unavailable checkout returns 503.
- **Consistency:** the read model has a 60 s TTL as a safety net in case the projector's invalidation does not arrive.
- To watch: memory usage and `evicted_keys`; if one service's keys get evicted by another's, we go back to the
  "instance per service" option.

## In the repository

[RedisServiceCollectionExtensions.cs](../../src/BuildingBlocks/ShopFlow.Azure/Redis/RedisServiceCollectionExtensions.cs),
[redis.bicep](../../infra/modules/data/redis.bicep), the prefix table in [00-overview.md](../architecture/00-overview.md) (section 7.3).
