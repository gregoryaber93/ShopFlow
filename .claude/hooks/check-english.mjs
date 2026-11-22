#!/usr/bin/env node
// PostToolUse hook (Write, Edit, MultiEdit): reports Polish text in the file that was just written.
//
// WHY: the repository language is English (CLAUDE.md, "Language"). Catching a Polish comment right after the edit is
// cheaper than a repository-wide scan later. Deliberate exceptions live in scripts/english-allowlist.json.

import { existsSync } from "node:fs";
import { readStdinJson, block, allow, editedPath, repositoryPath, repositoryRoot } from "./lib/hook-io.mjs";
import { isTextFile, loadAllowlist, scanFile } from "../../scripts/lib/language-check.mjs";

const input = await readStdinJson();
const absolutePath = editedPath(input);

if (!absolutePath || !existsSync(absolutePath) || !isTextFile(absolutePath)) allow();

const repoPath = repositoryPath(absolutePath);
if (!repoPath) allow(); // Outside the repository (scratch files, memory): not our business.

const findings = scanFile(repositoryRoot, absolutePath, loadAllowlist(repositoryRoot));
if (findings.length === 0) allow();

const listed = findings
  .slice(0, 10)
  .map((finding) => `  ${repoPath}:${finding.line} (${finding.reason}): ${finding.text.slice(0, 120)}`)
  .join("\n");
block(
  `check-english: ${findings.length} line(s) with Polish text in ${repoPath}. The repository is English-only; ` +
    `translate them. If the text is customer-facing and must stay Polish, add the file to ` +
    `scripts/english-allowlist.json with a reason.\n${listed}`,
);
