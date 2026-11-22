---
paths:
  - "docs/**"
  - "**/*.md"
---

# Documentation rules

The documentation is half of this repository's value: it explains what runs where and why. Keep it accurate.

## Structure

| Folder | Purpose | Update when |
|---|---|---|
| `docs/architecture/` | overview, C4, message flows, saga, network, identity/RBAC, patterns catalog | a component, flow, role or pattern changes |
| `docs/walkthroughs/` | scenario-driven "what executes where", step by step with file paths | a flow touched by the scenario changes |
| `docs/adr/` | decisions (Context, Decision, Options, Consequences) | a significant choice is made or reversed (`/write-adr`) |
| `docs/runbooks/` | what on-call does for each alert | an alert, metric or operator tool changes |
| `docs/kql/` | queries used by runbooks and the workbook | telemetry names or dimensions change |
| `docs/cost.md` | cost drivers per environment | a SKU, capacity or retention parameter changes |
| `README.md` per deployable | functions/endpoints, settings, links to walkthroughs | triggers, settings or routes change |

## Style

- English, plain and precise; present tense; second person only in runbooks and walkthrough instructions.
- Name real things: file paths as relative links, class and function names in backticks, Azure resource names using
  the naming convention (`sbns-shopflow-prod-weu`). Never invent a component that is not in the code.
- Diagrams in Mermaid (C4 via `flowchart`/`sequenceDiagram`); keep node names identical to function and queue names.
- ADRs are immutable once accepted: supersede with a new ADR instead of rewriting history (status `Superseded by`).
- Headings are link targets (`#anchor`); renaming a heading means fixing the links. Run `node scripts/check-links.mjs`.
- Commands that would create Azure resources are shown as documentation only, with a note that they cost money.

The `docs-sync` agent compares code changes with the docs and lists what is stale.
