## What and why

<!-- One or two sentences. A link to the issue or ADR, if there is one. -->

## Backward compatibility

- [ ] Message contracts: additive changes only, or a new contract version (`V2`) next to the old one
- [ ] Pacts: if the consumer tests changed a file in `tests/ShopFlow.Pact.Tests/pacts`, it is in this PR
- [ ] Database migrations: the schema works with both the old and the new application version (expand/contract, no column removal in the same release)
- [ ] New behavior behind a feature flag if a rollback cannot safely withdraw it

## Infrastructure and operations

- [ ] What-if (the `infra-validate` comment) reviewed: no unexpected `Delete` or `Modify`
- [ ] New identity, queue or subscription: roles granted in Bicep, topology in `messaging-topology.json`
- [ ] New dependency or metric: alert, runbook or KQL query updated

## Documentation

- [ ] Architecture docs, walkthroughs or an ADR updated if a flow, a pattern or a decision changed

## Tests

<!-- What was checked: unit, contract, integration (Docker), manually in dev. `scripts/verify.ps1` passes locally. -->
