---
paths:
  - "infra/**"
  - "ps-rule.yaml"
---

# Infrastructure rules (Bicep)

Nothing in this folder is ever deployed from this machine: no `az login`, `az deployment`, what-if or Bicep CLI
download (the user's zero-cost constraint). Bicep is reviewed by reading it; the first compilation happens in
`.github/workflows/infra-validate.yml`. Overview: [infra/README.md](../../infra/README.md).

## Structure

- `bootstrap/` (administrator, once): resource groups, pipeline identities with OIDC federated credentials, custom
  roles, constrained RBAC delegation, budgets. `main.bicep` (pipeline): layers `core`, `data`, `messaging`, `apps`,
  `edge`, `operations` in three resource groups.
- `shared/types.bicep` defines `environmentConfig`; everything that differs per environment goes there and into
  `parameters/<env>.bicepparam`. Topology, roles and network shape are identical in every environment.
- Names come from `resourceName()` / `compactName()` in `shared/naming.bicep`, tags from `tags()`. No
  hand-written resource names.

## Security defaults (non-negotiable)

- Zero keys: `disableLocalAuth: true`, `allowSharedKeyAccess: false`, Entra-only SQL authentication. Application
  settings contain endpoints and identity `clientId`s only. Third-party secrets: Key Vault + App Configuration
  references.
- Private endpoints and `publicNetworkAccess: 'Disabled'` for data services; exceptions are listed with a compensating
  control in [network.md](../../docs/architecture/network.md).
- User-assigned identities are created in the core layer before compute, and role assignments sit next to the
  **resource** at the narrowest scope (queue, subscription, container, database, task hub), with deterministic names
  from `roleAssignmentName()`. The pipeline may grant only roles listed in `shared/roles.bicep` (`delegableRoles`).
- Diagnostic settings to Log Analytics on every resource that supports them.

## Style

- API versions: the newest generally available; a preview version needs a `// AZURE:` comment saying why.
- `@description()` on every parameter and output; user-defined types instead of `object` where the shape is known.
- No unescaped apostrophes in strings (`\'`); prefer single-line ternaries; loop arrays must be known at the start of
  the deployment (computed ids go into separate maps).
- Messaging entities come from `modules/messaging/messaging-topology.json`; never hard-code a queue or topic name.
- Cost-relevant settings (SKU, capacity, autoscale caps, retention) are parameters, and [docs/cost.md](../../docs/cost.md)
  is updated when they change.

Use the `/add-azure-resource` skill for new resources and the `bicep-reviewer` agent for reviews.
