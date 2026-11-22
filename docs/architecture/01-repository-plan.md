# ShopFlow — repository plan

| | |
|---|---|
| **Status** | Accepted; stages 0–14 completed |
| **Date** | 2026-11-14 (updated 2026-11-22) |
| **Companion document** | [00-overview.md](00-overview.md) — architecture |

---

## 1. Directory tree

```text
ShopFlow/
├── .claude/                              # Claude Code configuration (see CLAUDE.md)
│   ├── settings.json                     # shared permissions and hooks
│   ├── hooks/                            # guard-cloud-commands, protect-generated-files, check-english (Node.js)
│   ├── rules/                            # path-scoped rules: C#, messaging, contracts, persistence, Bicep, Actions, tests, docs
│   ├── agents/                           # subagents: architecture, contracts, Bicep reviewers, incident responder, docs sync, Azure explainer
│   └── skills/                           # verify, add-message-contract, add-message-consumer, ef-migration,
│                                         # add-azure-resource, write-adr, trace-flow
│
├── .github/                              # details: .github/README.md
│   ├── workflows/
│   │   ├── ci.yml                        # PR, merge queue, call from release: build, tests, Pact, EF Core, integration
│   │   ├── security.yml                  # CodeQL, NuGet audit, dependency review, gitleaks, Trivy, PSRule
│   │   ├── infra-validate.yml            # PR: bicep build + lint, what-if against dev as a comment
│   │   ├── release.yml                   # main: ci → build → dev → test → prod
│   │   ├── rollback.yml                  # manual: slot swap or the previous image
│   │   ├── contracts-publish.yml         # tag contracts/v*: NuGet package with API compatibility validation
│   │   ├── _build.yml                    # reusable: zip packages, images + Trivy + attestation
│   │   └── _deploy.yml                   # reusable: one environment in one job on a runner in the VNet
│   ├── actions/                          # composite: setup-dotnet, azure-login, bicep-deploy, run-migrations,
│   │                                     # deploy-containerapp, deploy-appservice-slot, deploy-functionapp, smoke-test
│   ├── scripts/                          # current-image-tags.sh
│   ├── filters.yml                       # paths → deployable units
│   ├── deployables.json                  # unit → project or Dockerfile
│   ├── CODEOWNERS
│   ├── dependabot.yml
│   └── pull_request_template.md
│
├── docs/
│   ├── architecture/
│   │   ├── 00-overview.md                # target architecture, decisions D1–D8
│   │   ├── 01-repository-plan.md         # this document
│   │   ├── c4-context.md
│   │   ├── c4-container.md
│   │   ├── c4-component-orders.md        # + saga, inventory, payments
│   │   ├── message-flows.md              # who publishes, who subscribes
│   │   ├── saga.md                       # state machine, compensations, timeouts
│   │   ├── network.md
│   │   ├── identity-rbac.md              # the full role matrix
│   │   └── patterns.md                   # catalog of patterns with pointers to code and walkthroughs
│   ├── walkthroughs/                     # step-by-step scenarios: what executes where
│   ├── adr/                              # README (index, template), 0001 … 0016
│   ├── runbooks/                         # README (alert → runbook), dlq-growth, cosmos-throttling, payment-failure,
│   │                                     # secret-rotation, deployment-rollback
│   ├── kql/                              # order-trace, errors-by-service, messaging, dead-letters, outbox, queues,
│   │                                     # cosmos-ru, saga-funnel, payments
│   └── cost.md
│
├── infra/                                # details: infra/README.md
│   ├── main.bicep                        # targetScope = 'subscription'; existing resource groups, layers
│   ├── bicepconfig.json                  # linter rules
│   ├── bootstrap/                        # administrator: registry, GitHub OIDC identities, permissions, groups, lock, budget
│   ├── shared/
│   │   ├── naming.bicep                  # naming and tagging functions (user-defined functions)
│   │   ├── types.bicep                   # environment configuration types (user-defined types)
│   │   └── roles.bicep                   # built-in role identifiers, the list of deployable units
│   ├── layers/                           # compositions called from main.bicep
│   │   ├── core.bicep                    # network, DNS, monitoring, identities
│   │   ├── data.bicep                    # Cosmos, SQL, Redis, Storage, App Configuration, Key Vault, ACS
│   │   ├── messaging.bicep               # Service Bus, Event Hubs, Event Grid
│   │   ├── apps.bicep                    # App Service, Container Apps, Functions, Durable Task Scheduler
│   │   ├── edge.bicep                    # API Management, Front Door, WAF
│   │   └── operations.bicep              # alerts, action group, workbook
│   ├── modules/
│   │   ├── network/                      # vnet (+ NSG, NAT Gateway), private-dns-zones, private-endpoint, container-apps-dns,
│   │   │                                 # github-runners (GitHub runner networking in the VNet)
│   │   ├── monitoring/                   # observability (Log Analytics + Application Insights)
│   │   ├── identity/                     # identities, acr-pull
│   │   ├── messaging/                    # servicebus, eventhubs, eventgrid-invoices,
│   │   │                                 # messaging-topology.json (single source of truth for Bicep, tests and shopflow-dlq)
│   │   ├── data/                         # cosmos, sql, redis, storage (+ lifecycle)
│   │   ├── config/                       # app-configuration (+ feature flags, Key Vault references), key-vault
│   │   ├── communication/                # communication-services (ACS + Email + domain)
│   │   ├── compute/                      # app-service (+ slot, autoscale), container-apps-environment, container-app,
│   │   │                                 # container-app-job, function-app-flex, durable-task-scheduler
│   │   ├── edge/                         # apim, apim-gateway-policy, front-door (+ WAF)
│   │   └── operations/                   # alerts (+ workbook)
│   ├── parameters/
│   │   ├── dev.bicepparam
│   │   ├── test.bicepparam
│   │   └── prod.bicepparam
│   ├── apim/policies/                    # global.xml, customer-api.xml, catalog.xml, clickstream.xml, webhooks
│   ├── workbooks/                        # shopflow-operations.workbook.json
│   ├── sql/                              # grant-managed-identities.sql (identity users and roles)
│   └── scripts/                          # bootstrap-entra-apps.sh
│
├── scripts/                              # developer scripts: verify (mirrors CI locally), check-links, check-english
│
├── src/
│   ├── BuildingBlocks/
│   │   ├── ShopFlow.SharedKernel/        # Entity, AggregateRoot, DomainEvent, Result, Money
│   │   ├── ShopFlow.Contracts/           # V1/V2 message contracts, envelope, type catalog
│   │   ├── ShopFlow.Messaging/           # abstractions: publisher, outbox, inbox, failure classification
│   │   ├── ShopFlow.Messaging.ServiceBus/# envelope mapping, publisher, DLQ, retry configuration
│   │   ├── ShopFlow.Persistence.Cosmos/  # unit of work on TransactionalBatch, outbox, inbox, ETag
│   │   ├── ShopFlow.Persistence.SqlServer/ # outbox and inbox on EF Core, poller
│   │   ├── ShopFlow.Azure/               # credentials, client factories, App Configuration, Key Vault, Redis, SAS links
│   │   ├── ShopFlow.Resilience/          # Polly pipelines, named profiles, service-to-service tokens
│   │   ├── ShopFlow.Observability/       # OpenTelemetry, ActivitySource, metrics, health checks
│   │   ├── ShopFlow.Web/                 # Idempotency-Key, ETag/If-Match, ProblemDetails, two-issuer auth
│   │   ├── ShopFlow.Functions/           # Service Bus trigger adapter, Functions host defaults, Easy Auth principal
│   │   └── tests/
│   │       └── ShopFlow.BuildingBlocks.Tests/
│   │
│   └── Services/
│       ├── Orders/
│       │   ├── ShopFlow.Orders.Domain/
│       │   ├── ShopFlow.Orders.Application/
│       │   ├── ShopFlow.Orders.Infrastructure/
│       │   ├── ShopFlow.Orders.Api/              # App Service
│       │   ├── ShopFlow.Orders.Processors/       # Functions: outbox relay, shipping events
│       │   └── tests/                            # Unit, Api (component), Integration, Testing (fakes)
│       ├── OrderSaga/
│       │   ├── ShopFlow.OrderSaga/               # the workflow, activities and messaging as a plain library
│       │   ├── ShopFlow.OrderSaga.Functions/     # Durable Functions host
│       │   └── tests/
│       ├── Inventory/
│       │   ├── ShopFlow.Inventory.Domain/
│       │   ├── ShopFlow.Inventory.Application/
│       │   ├── ShopFlow.Inventory.Infrastructure/  # EF Core, migrations, Redis, Dockerfile.migrations
│       │   ├── ShopFlow.Inventory.Api/           # Container Apps
│       │   ├── ShopFlow.Inventory.Worker/        # Container Apps + KEDA
│       │   └── tests/
│       ├── Payments/                             # Domain, Application, Infrastructure, Functions, tests
│       ├── Shipping/                             # Domain, Application, Infrastructure, Functions, tests
│       ├── Notifications/                        # Application, Infrastructure, Functions, tests
│       ├── CustomerReadModels/                   # Application, Infrastructure, Projections (Functions), Api, tests
│       └── Analytics/                            # Application, Infrastructure, Functions, tests
│
├── tests/                                # cross-cutting tests
│   ├── ShopFlow.Contracts.Tests/         # schema snapshots, backward compatibility, topology consistency
│   ├── ShopFlow.Pact.Tests/              # consumer–provider pacts (messages and HTTP), files in pacts/
│   ├── ShopFlow.Architecture.Tests/      # dependency rules between layers and contexts
│   └── ShopFlow.Testing/                 # shared test authentication and helpers
│
├── tools/
│   ├── ShopFlow.DlqReplay/               # CLI for inspecting and replaying DLQs (shopflow-dlq)
│   └── ShopFlow.DlqReplay.Tests/
│
├── .editorconfig
├── .gitattributes
├── .gitignore
├── CLAUDE.md                             # instructions for Claude Code
├── Directory.Build.props                 # shared build settings, analyzers, nullable
├── Directory.Packages.props              # Central Package Management
├── global.json                           # .NET 10 SDK pin, Microsoft.Testing.Platform
├── nuget.config
├── ps-rule.yaml                          # PSRule for Azure (security.yml)
├── ShopFlow.slnx
└── README.md
```

**Why service tests live next to the services:** CI path filters and CODEOWNERS entries then cover a whole bounded
context with one pattern (`src/Services/Orders/**`). Only tests that by definition cross contexts stay in `tests/`.

## 2. Technology stack

| Area | Choice |
|---|---|
| Platform | .NET 10 (LTS), C# 14, `Nullable` and `TreatWarningsAsErrors` enabled |
| API | ASP.NET Core Minimal APIs, JwtBearer with explicit validation of tokens from two tenants (instead of `Microsoft.Identity.Web`, which hides these details), OpenAPI |
| Functions | isolated worker model, Durable Functions with the Durable Task Scheduler backend |
| Messaging | `Azure.Messaging.ServiceBus`, `Azure.Messaging.EventHubs`, `Azure.Messaging.EventGrid` |
| Data | `Microsoft.Azure.Cosmos`, EF Core 10 + `Microsoft.Data.SqlClient`, `StackExchange.Redis` with Entra authentication, `HybridCache`, `Azure.Storage.Blobs` |
| Identity and configuration | `Azure.Identity`, `Microsoft.Extensions.Azure`, the App Configuration provider, `Microsoft.FeatureManagement` |
| Resilience | `Microsoft.Extensions.Http.Resilience` (Polly v8) and SDK client retry options |
| Observability | OpenTelemetry with Azure Monitor exporters (the same code in APIs, containers and Functions) |
| Validation | FluentValidation |
| Tests | xUnit v3 on Microsoft.Testing.Platform, Shouldly, NSubstitute, Testcontainers, custom JSON schema snapshots (no Verify library, which requires a licence declaration), PactNet, NetArchTest |
| Infrastructure | Bicep (`.bicepparam`), PSRule for Azure |
| CI/CD | GitHub Actions, OIDC, GitHub environments with required reviewers |
| AI-assisted development | Claude Code: `CLAUDE.md`, path-scoped rules, subagents, skills, hooks (`.claude/`) |

Package versions are pinned centrally in `Directory.Packages.props`.

## 3. Conventions

**Explanatory comments** — only where the code does not explain the decision by itself:

```csharp
// AZURE: TransactionalBatch works only within one logical partition
//        and one container (limit of 100 operations, 2 MB).
// WHY:   That is why the outbox record has the same partition key as the aggregate — otherwise
//        writing the order and the event would not be atomic.
```

The same markers in Bicep (`// AZURE:`), YAML (`# AZURE:`) and APIM policies (`<!-- AZURE: -->`). Everything is in English.

**Service layers** (dependencies point inwards, enforced by architecture tests):

```text
Host (Api / Functions / Worker)  ->  Infrastructure  ->  Application  ->  Domain
                                                          Application  ->  Contracts
```

`Domain` knows nothing about Azure. `Application` defines ports. `Infrastructure` contains the adapters to Azure services.
The host composes dependencies and configuration.

**Configuration:** section names follow the `Microsoft.Extensions.Azure` convention (`ServiceBus:fullyQualifiedNamespace`,
`Cosmos:accountEndpoint`), i.e. endpoint addresses only, no secrets.

## 4. Stages

After every stage: a list of what was created and 3–5 places worth noticing during analysis.

| Stage | Scope | Main results |
|---|---|---|
| **0** | Architecture and plan | this document and `00-overview.md` |
| **1** | Repository skeleton, contracts, shared libraries | solution files, `ShopFlow.Contracts`, all `BuildingBlocks`, contract tests |
| **2** | Orders | Domain, Application, Infrastructure, `orders-api`, `orders-processors`, tests |
| **3** | Order Saga | Durable orchestrator, activities, compensations, reply dispatcher, orchestration tests |
| **4** | Inventory | catalog and stock in SQL, EF Core migrations, `inventory-api`, `inventory-worker`, Dockerfile |
| **5** | Payments | commands in sessions, PSP adapter with Polly, webhook, invoices, Blob event handling |
| **6** | Shipping | commands, carrier adapter, labels in Blob Storage, webhook |
| **7** | Notifications | `notifications-inbox` consumer, channels, feature flags |
| **8** | Customer Read Models | projections, `readmodels-api`, Redis cache |
| **9** | Analytics | clickstream ingestion, forwarder, Event Hubs aggregations |
| **10** | Cross-cutting tests and tools | Pact, architecture tests, `ShopFlow.DlqReplay` |
| **11** | Infrastructure | Bicep modules, layers, parameters for three environments, APIM policies, workbook |
| **12** | Pipelines | GitHub Actions workflows, composite actions, OIDC bootstrap (`infra/bootstrap`), runners in the VNet, migrations job |
| **13** | Operational documentation | C4, message flows, 16 ADRs, runbooks, KQL, `cost.md` |
| **14** | English and Claude Code tooling | the whole repository in English; `CLAUDE.md`, rules, agents, skills, hooks, scripts; walkthroughs and the patterns catalog |

**Depth:** Orders, Order Saga, Inventory and Payments are built in full depth (they carry most of the patterns).
Shipping, Notifications, Customer Read Models and Analytics are complete but leaner — without repeating what was shown earlier.

## 5. Verification

The machine has the .NET SDK 10.0.404 and the Azure CLI:

- after every code stage `dotnet build` and the unit tests run locally (`scripts/verify.sh` mirrors the CI steps);
- Bicep is not compiled locally (no Bicep CLI by choice); the first compilation and lint happen in
  `.github/workflows/infra-validate.yml`;
- integration tests (Testcontainers, they need Docker and emulators) and deployments are not run — this is always stated
  explicitly in the stage summary. Nothing in this repository is meant to incur Azure costs.
