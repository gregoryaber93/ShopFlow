# ADR-0001: Monorepo with independently deployed services

| Status | Date | Deciders |
|---|---|---|
| Accepted | 2026-11-14 | architecture, platform |

## Context

ShopFlow has seven bounded contexts, twelve deployable units and shared libraries (`BuildingBlocks`, message
contracts). There are several teams (checkout, fulfilment, customer, data), but cross-cutting changes are frequent: a
new field in a contract requires a change in the publisher, the consumer, the pact and the topology. Every service must
deploy independently, without waiting for a release of the whole.

## Decision

One Git repository with one solution (`ShopFlow.slnx`) and independently deployed units. Boundaries are enforced by
code (architecture tests), not by repository boundaries. The pipeline builds everything, but deploys only the units
that changed since the last deployment to a given environment.

## Options considered

| Option | Pros | Cons |
|---|---|---|
| **Monorepo, independent deployments** (chosen) | a contract change, both sides and the pact in one pull request; one version of each package (Central Package Management); a cross-cutting refactoring in one commit | CI must know what changed; temptation to take shortcuts between contexts |
| Repository per service | hard isolation, separate permissions and history | contracts as versioned NuGet packages; a cross-cutting change is several PRs in a fixed order; library versions drift apart |
| Monorepo, one artifact (modular monolith) | simplest CI/CD, no distributed-network problems | shared deployment and scaling cycle; does not show the Azure services this project is about |

## Consequences

- **Positive:** a contract change is visible in one diff together with the schema snapshot and the pact file;
  CODEOWNERS requires approval from the owners of both sides.
- **Negative:** CI builds and tests the whole solution for every pull request (minutes, not seconds).
  Integration tests and deployments are selective: [filters.yml](../../.github/filters.yml).
- **Risk:** a service referencing another context's code. Blocked by the tests in `tests/ShopFlow.Architecture.Tests`
  (a context depends only on `BuildingBlocks` and `ShopFlow.Contracts`).
- **Consumers outside the repository** get the contracts as a NuGet package with API compatibility validation
  ([contracts-publish.yml](../../.github/workflows/contracts-publish.yml)).

## In the repository

- [`.github/filters.yml`](../../.github/filters.yml), [`.github/deployables.json`](../../.github/deployables.json): paths → units.
- [`.github/CODEOWNERS`](../../.github/CODEOWNERS): owners per context; tests live next to the service, so one pattern covers code and tests.
- The `deployedCommit` tag on the `rg-…-apps` group: [_deploy.yml](../../.github/workflows/_deploy.yml).
