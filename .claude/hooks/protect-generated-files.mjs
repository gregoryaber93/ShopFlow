#!/usr/bin/env node
// PreToolUse hook (Write, Edit, MultiEdit, NotebookEdit): refuses hand edits of generated files and build output.
//
// WHY: these files are produced by tools and checked by tests or CI. A hand edit silently diverges from what the
// tool would generate (EF model snapshot vs. model, pact vs. consumer test, schema snapshot vs. contract) and the
// next regeneration either overwrites it or CI fails far from the cause.
// Override: SHOPFLOW_ALLOW_GENERATED_EDIT=1 (for example to resolve a merge conflict in a snapshot).

import { readStdinJson, deny, allow, editedPath, repositoryPath } from "./lib/hook-io.mjs";

const PROTECTED = [
  {
    test: (p) => /\/Migrations\/.+\.Designer\.cs$/.test(p) || /ModelSnapshot\.cs$/.test(p),
    how: "EF Core generates it. Change the model and run `dotnet ef migrations add` (skill /ef-migration); to redo an undeployed migration use `dotnet ef migrations remove`.",
  },
  {
    test: (p) => /^tests\/ShopFlow\.Pact\.Tests\/pacts\/.+\.json$/.test(p),
    how: "Pact consumer tests generate it. Change the consumer test and run `dotnet test --project tests/ShopFlow.Pact.Tests --filter-namespace \"ShopFlow.Pact.Tests.Consumers\"`.",
  },
  {
    test: (p) => /\.approved\.json$/.test(p),
    how: "The schema snapshot tests generate it. Run `ShopFlow.Contracts.Tests` with SHOPFLOW_ACCEPT_SNAPSHOTS=1 and review the diff.",
  },
  {
    test: (p) => /(^|\/)(bin|obj|TestResults|artifacts)\//.test(p),
    how: "This is build or test output, not source.",
  },
];

const input = await readStdinJson();
const absolutePath = editedPath(input);

if (!absolutePath || process.env.SHOPFLOW_ALLOW_GENERATED_EDIT === "1") {
  allow();
} else {
  const repoPath = repositoryPath(absolutePath);
  const rule = repoPath ? PROTECTED.find((candidate) => candidate.test(repoPath)) : undefined;
  if (rule) {
    deny(`Blocked by .claude/hooks/protect-generated-files.mjs: ${repoPath} is generated. ${rule.how}`);
  } else {
    allow();
  }
}
