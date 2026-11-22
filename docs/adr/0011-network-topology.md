# ADR-0011: Network topology and exceptions to private endpoints

| Status | Date | Deciders |
|---|---|---|
| Accepted (extended with the runner subnet on 2026-11-22) | 2026-11-14 | platform, security |

## Context

Customer data (orders, addresses, payments) must not be reachable from the internet, even if credentials leak. At the
same time some platform services (Event Grid, Event Hubs Capture, Azure Monitor, Communication Services) run outside the
VNet, and the pipeline must deploy code to applications without a public endpoint.

## Decision

- **One `/22` VNet per environment** with six subnets, without a hub or a firewall.
- **A private endpoint** for every data store, broker, Key Vault, App Configuration, `orders-api` (with its slot) and
  every function app. Private DNS zones linked to the VNet.
- **The only public entry: Front Door Premium with WAF**, connected to APIM through Private Link. APIM Standard v2 with
  outbound VNet integration and public access disabled once the connection is approved.
- **GitHub Actions runners in the VNet** (`snet-runners`, `GitHub.Network/networkSettings`) for deployments through SCM.
- **Exceptions** with a justification and a compensating control: [network.md](../architecture/network.md#deliberate-exceptions-to-everything-private).

## Options considered

| Option | Pros | Cons |
|---|---|---|
| **A single VNet, private endpoints** (chosen) | data without public endpoints; a simple topology; the same shape in every environment | the cost of private endpoints and DNS zones; deployments need network access |
| Hub-and-spoke with Azure Firewall | inspection and an FQDN allowlist for outbound traffic; shared services | a firewall costs ~1k USD/month per hub; routes, rules and a network team |
| Service endpoints instead of private endpoints | cheap, no DNS | traffic goes to the services' public addresses; no protection against exfiltration to someone else's accounts |
| APIM Premium in internal mode + Application Gateway | full L7 control, multiple regions | APIM Premium ~2.8k USD/month per unit; a second WAF |
| Self-hosted runners (Container Apps Jobs + KEDA) | works without GitHub Enterprise | the team maintains the runner image, its updates and the GitHub App key (decision D6) |

## Consequences

- About 25 private endpoints per environment: a fixed cost and a data processing charge ([cost.md](../cost.md)).
- **Order on the first deployment:** the Front Door → APIM Private Link connection must be approved before APIM public
  access is disabled (`APIM_PRIVATE_ONLY`), and the VNet runners are created by the same deployment that later uses them
  ([infra/README.md](../../infra/README.md), bootstrap).
- **No outbound traffic inspection:** the control is the identity on the target services' side and the NAT Gateway with
  a fixed IP. In an organization with a PCI DSS requirement a hub with a firewall is added.
- Diagnostics from a laptop do not work: operator access through Azure Bastion or a job in the VNet (e.g. `shopflow-dlq`).

## In the repository

[vnet.bicep](../../infra/modules/network/vnet.bicep), [private-dns-zones.bicep](../../infra/modules/network/private-dns-zones.bicep),
[private-endpoint.bicep](../../infra/modules/network/private-endpoint.bicep), [front-door.bicep](../../infra/modules/edge/front-door.bicep),
[github-runners.bicep](../../infra/modules/network/github-runners.bicep), [network.md](../architecture/network.md).
