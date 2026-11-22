---
name: write-adr
description: Writes an Architecture Decision Record in docs/adr using the repository template (context, decision, options with pros and cons, consequences), numbers it, links it from the ADR index and the overview, and supersedes an older ADR when a decision changes. Use when a significant technical choice is made, reversed, or needs to be explained.
argument-hint: "<decision title>"
---

# Write an ADR

ADRs record decisions that are expensive to reverse: a platform service, a data store, a consistency model, a
contract policy, a security boundary, a deployment strategy. A bug fix or a library upgrade is not an ADR.

## Steps

1. Read [docs/adr/README.md](../../../docs/adr/README.md) (index and template) and two existing ADRs close to the topic
   for tone and depth, e.g. [0005](../../../docs/adr/0005-service-bus-event-hubs-event-grid.md) and
   [0009](../../../docs/adr/0009-outbox-and-inbox.md).
2. Number: the next free `NNNN` after the highest file in `docs/adr/`. File name: `NNNN-kebab-case-title.md`.
3. Write it with the template:
   - **Header table:** Status (`Proposed` unless the user says it is accepted), Date (today, `YYYY-MM-DD`), Deciders.
   - **Context:** the problem and the forces (requirements, constraints, cost, team skills), with numbers where they
     exist (throughput, latency, RU, price).
   - **Decision:** one paragraph in the indicative mood ("We use ..."), concrete enough to check in code.
   - **Options considered:** a table `| Option | Pros | Cons |` with the chosen one marked `(chosen)`; at least two
     real alternatives.
   - **Consequences:** positive and negative, including operational and cost ones, and what would make us revisit it.
   - **In the repository:** relative links to the files, Bicep modules and docs that implement it.
4. Superseding: never rewrite an accepted ADR. Set the old one's status to `Superseded by ADR-NNNN` (the only edit
   allowed) and start the new one with `Supersedes ADR-00xx`.
5. Update the index table in `docs/adr/README.md`, the decisions table in `docs/architecture/00-overview.md`, and
   `docs/architecture/patterns.md` if the decision introduces or retires a pattern.
6. Run `node scripts/check-links.mjs` and `node scripts/check-english.mjs`.
