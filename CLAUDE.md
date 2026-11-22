# ShopFlow: instructions for Claude Code

ShopFlow is an enterprise-grade e-commerce platform on Azure (.NET 10 microservices, Bicep, GitHub Actions) kept as a
**learning repository for code analysis**. It mirrors a production system, but it is never run in the cloud.

## Hard constraints (never break these)

1. **Zero cost.** Never run anything that creates, changes or bills Azure or other cloud resources: `az login`,
   `az deployment ...`, `az ... create`, `azd up`, `terraform apply`, `func azure functionapp publish`, `docker push`,
   what-if against a real subscription, paid APIs. The `guard-cloud-commands` hook blocks these; do not work around it.
2. **Nothing leaves the machine.** No `git push`, no publishing packages, no uploading files. The folder may not even be
   a git repository; do not run `git init` or commit unless the user asks.
3. **No downloads of new tooling** (e.g. the Bicep CLI) unless the user explicitly asks. NuGet restore is fine.
4. **Telemetry off:** `DOTNET_CLI_TELEMETRY_OPTOUT=1`, `PACT_DO_NOT_TRACK=true` (the scripts set them).
5. **Private data stays private:** never put the user's name, e-mail or other personal details in files or requests.

Allowed and expected: local `dotnet restore/build/test/format`, `dotnet ef` commands that do not touch a database,
the scripts in `scripts/`, reading anything in the repository.

## Language

Everything in the repository is **English**: code, comments, docs, ADRs, runbooks, commit messages, Claude config.
The only Polish text is customer-facing (localized e-mail/SMS templates, the Polish VAT invoice) and is listed in
[scripts/english-allowlist.json](scripts/english-allowlist.json). The `check-english` hook reports Polish text after
every edit. Talk to the user in the language they write in.

## Repository map

```text
src/BuildingBlocks/   shared libraries (Contracts, Messaging, Messaging.ServiceBus, Persistence.Cosmos/SqlServer,
                      Azure, Web, Functions, Observability, Resilience, SharedKernel) + their tests
src/Services/<Ctx>/   one bounded context: <Ctx>.Domain, .Application, .Infrastructure, hosts (.Api, .Functions,
                      .Processors, .Worker, .Projections) and tests/ (UnitTests, Api.Tests, IntegrationTests, Testing)
tests/                cross-cutting: Architecture.Tests, Contracts.Tests (schema snapshots, topology), Pact.Tests
tools/                ShopFlow.DlqReplay (operator tool) + tests
infra/                Bicep: bootstrap/ (admin, once), main.bicep + layers/ + modules/ (pipeline), parameters/
.github/              workflows (ci, security, infra-validate, release, rollback, contracts-publish), composite actions
docs/                 architecture/ (overview, C4, flows, saga, network, RBAC, patterns), walkthroughs/, adr/,
                      runbooks/, kql/, cost.md
scripts/              verify.ps1 / verify.sh (local CI), check-english.mjs, check-links.mjs
.claude/              rules/ (path-scoped), agents/, skills/, hooks/, settings.json
```

Start reading at [docs/architecture/00-overview.md](docs/architecture/00-overview.md),
[docs/architecture/patterns.md](docs/architecture/patterns.md) and [docs/walkthroughs](docs/walkthroughs/README.md).

## Commands

```bash
./scripts/verify.sh                       # everything CI checks without the cloud (or scripts/verify.ps1 on Windows)
dotnet build ShopFlow.slnx                # TreatWarningsAsErrors: a warning fails the build
dotnet test --solution ShopFlow.slnx --filter-not-namespace "ShopFlow.Pact.Tests*" --ignore-exit-code 8
dotnet test --project tests/ShopFlow.Pact.Tests --filter-namespace "ShopFlow.Pact.Tests.Consumers"   # then .Providers
dotnet format whitespace ShopFlow.slnx --verify-no-changes
dotnet tool restore && dotnet ef migrations has-pending-model-changes --project src/Services/Inventory/ShopFlow.Inventory.Infrastructure
node scripts/check-english.mjs            # Polish text outside the allowlist
node scripts/check-links.mjs              # broken relative links and anchors in Markdown
```

- Tests run on Microsoft.Testing.Platform (xUnit v3): use `--filter-*` options, not `--filter "FullyQualifiedName~"`.
  Exit code 8 means "zero tests ran" (the Pact project when filtered out).
- Integration tests (Testcontainers, emulators) skip themselves unless `SHOPFLOW_RUN_INTEGRATION_TESTS=1` and Docker run.
- Contract schema snapshots: after a deliberate contract change run the tests with `SHOPFLOW_ACCEPT_SNAPSHOTS=1` and
  review the `*.approved.json` diff. Never hand-edit snapshots or pact files.

## Architecture invariants

Enforced by `tests/ShopFlow.Architecture.Tests` and `tests/ShopFlow.Contracts.Tests`; details in `.claude/rules/`.

- **Layers:** Domain → SharedKernel only. Application → no persistence or Azure SDKs (ports only). Infrastructure
  implements ports with `internal sealed` adapters. A context references only BuildingBlocks and itself; contexts talk
  through message contracts or HTTP, never through each other's assemblies.
- **Messaging:** state change + message in one transaction (outbox: Cosmos `TransactionalBatch` + Change Feed relay, or
  SQL table + poller). Every handler is idempotent (inbox / natural idempotency) and `sealed`. Failures are classified
  as transient (retry) or permanent (DLQ); never swallow exceptions to "complete" a message.
- **Contracts** (`ShopFlow.Contracts`): `sealed record`, `[MessageContract(name, version)]`, commands in the imperative,
  events in the past tense. Additive changes only within a version; a breaking change is a new `V<n>` namespace
  published side by side, with an upcaster where possible ([ADR-0012](docs/adr/0012-contract-versioning.md)).
- **Topology as data:** queues, topics, subscriptions, rules and senders live in
  `infra/modules/messaging/messaging-topology.json`, mirrored by `MessagingTopology.cs`; tests keep them in sync.
- **Zero keys:** managed identities everywhere, `disableLocalAuth`, roles at the narrowest scope (queue, container,
  database). Third-party secrets only in Key Vault, read through App Configuration references.
- **Saga** (Durable Functions + DTS): orchestration, not choreography; compensations registered before each step and
  state-tolerant; orchestrator code deterministic (no `DateTime.Now`, I/O or random in the orchestrator).

## Conventions

- `// AZURE:` explains a behavior or limit of an Azure service; `// WHY:` justifies a design decision. Keep them: they
  are the point of this repository. New non-obvious code gets one of them.
- C#: file-scoped namespaces, primary constructors, `sealed` by default, `TimeProvider` instead of `DateTime.UtcNow`,
  `CancellationToken` passed everywhere, `ConfigureAwait(false)` in libraries, `LoggerMessage` source-generated logs,
  `Result<T>` for expected business failures and exceptions for the unexpected.
- Central Package Management: versions only in `Directory.Packages.props`, never `Version=` in a `.csproj`.
- Tests: xUnit v3 + Shouldly; hand-written fakes and in-memory adapters for our own ports; NSubstitute only for SDK
  types that cannot be faked otherwise (Cosmos `Container`/`TransactionalBatch`, `DurableTaskClient`); `FakeTimeProvider`
  for time.
- Generated files are not edited by hand: EF migrations' `*.Designer.cs` and `*ModelSnapshot.cs`, `pacts/*.json`,
  `*.approved.json` (the `protect-generated-files` hook enforces this).

## Claude Code setup

- **Rules** (`.claude/rules/*.md`) load automatically for matching paths: csharp, messaging, contracts, persistence,
  infra-bicep, github-actions, testing, docs.
- **Skills** (`/name`): `verify`, `add-message-contract`, `add-message-consumer`, `ef-migration`, `add-azure-resource`,
  `write-adr`, `trace-flow`.
- **Agents:** `architecture-reviewer`, `contract-change-reviewer`, `bicep-reviewer`, `incident-responder`, `docs-sync`,
  `azure-explainer`.
- **Hooks** (`.claude/hooks/*.mjs`, Node.js): `guard-cloud-commands` (PreToolUse Bash), `protect-generated-files`
  (PreToolUse Edit/Write), `check-english` (PostToolUse Edit/Write).

## Definition of done

1. `scripts/verify` passes (format, build, tests, Pact, EF model check, English check, links).
2. A changed flow, pattern or decision is reflected in `docs/` (walkthrough, patterns catalog, ADR); the `docs-sync`
   agent can check this.
3. A changed contract, topology or role assignment is consistent across code, `messaging-topology.json` and Bicep.
4. Nothing was deployed, pushed or downloaded.
