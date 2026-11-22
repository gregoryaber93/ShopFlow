# ADR-0010: Managed identities and app roles

| Status | Date | Deciders |
|---|---|---|
| Accepted (extended with pipeline identities on 2026-11-22) | 2026-11-14 | architecture, security |

## Context

Twelve units connect to more than a dozen Azure services, two pairs of services talk over HTTP, and the pipeline deploys
to three subscriptions. Every secret (key, connection string, SQL password) means rotation, leak risk and the auditor's
question "who has access to this".

## Decision

1. **One user-assigned managed identity per deployable unit.**
2. **Roles at the narrowest scope**: a Service Bus entity, a Cosmos database, a Blob container, a task hub; never the
   namespace or the account, unless the service has no narrower scope.
3. **Local authentication disabled** on resources (keys, SAS, SQL passwords, the Application Insights key).
4. **Service-to-service HTTP**: a token from the managed identity (client credentials) with an Entra ID app role,
   checked in the API (`Orders.Internal.Write`, `Catalog.Prices.Read`).
5. **Secrets only for third parties** (PSP, carrier): Key Vault, a reference in App Configuration, the Key Vault Secrets
   User role only for the service that uses the secret.
6. **Pipeline through OIDC**: three kinds of identities with federated credentials (build, plan, deploy per
   environment), created in the bootstrap by an administrator.

## Options considered

| Option | Pros | Cons |
|---|---|---|
| **User-assigned per unit** (chosen) | roles granted before the compute exists; the slot and production share the identity; the identity survives recreating the application | more resources to manage |
| System-assigned | lifecycle tied to the application | roles only after the application exists (a race on the first deployment); a slot has a different identity |
| One identity per context | fewer identities | the API and the processor with the same rights; `orders-api` could send to the topic bypassing the outbox |
| Service-to-service HTTP without a token (network trust) | simple | any process in the VNet can call `/internal`; no audit trail |
| API keys or mTLS between services | independent of Entra | secrets or certificates to rotate |

## Consequences

- **Many role assignments** (around a hundred per environment). Service Bus computes them from the topology
  ([servicebus.bicep](../../infra/modules/messaging/servicebus.bicep)), so a new subscription gets its roles without manual work.
- **RBAC propagation** takes up to a few minutes: the first run after a role grant may get a 403 that goes away by
  itself. SDK clients retry, and the `ready` health check waits.
- **The role assignment limit per subscription** (4000) is far away, but grows with the number of entities; a
  subscription per environment helps.
- **The pipeline cannot grant itself permissions:** the RBAC Administrator with an ABAC condition allows granting only
  the roles on the `delegableRoles` list and only in the environment's groups ([identity-rbac.md](../architecture/identity-rbac.md#pipeline-github-actions-oidc)).
- **The customers tenant without a secret:** Notifications reads Graph in External ID through a federated credential
  that trusts the managed identity.

## In the repository

[identities.bicep](../../infra/modules/identity/identities.bicep), [roles.bicep](../../infra/shared/roles.bicep),
[infra/bootstrap](../../infra/bootstrap), [bootstrap-entra-apps.sh](../../infra/scripts/bootstrap-entra-apps.sh),
[AuthenticationExtensions.cs](../../src/BuildingBlocks/ShopFlow.Web/Authentication/AuthenticationExtensions.cs),
the full matrix: [identity-rbac.md](../architecture/identity-rbac.md).
