#!/usr/bin/env node
// Reports non-English (Polish) text in the repository. Exit code 1 when anything is found.
//
// Usage:
//   node scripts/check-english.mjs                 whole repository
//   node scripts/check-english.mjs <file> [...]    selected files (paths relative to the repository root or absolute)
//
// Deliberate exceptions (customer-facing Polish strings) are listed in scripts/english-allowlist.json.

import { resolve, dirname, isAbsolute } from "node:path";
import { fileURLToPath } from "node:url";
import { existsSync } from "node:fs";
import { loadAllowlist, scanFile, walk, toRepoPath, isTextFile } from "./lib/language-check.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const allowlist = loadAllowlist(root);

const files = process.argv.length > 2
  ? process.argv.slice(2).map((p) => (isAbsolute(p) ? p : resolve(root, p))).filter((p) => existsSync(p) && isTextFile(p))
  : [...walk(root)];

let total = 0;
for (const file of files) {
  const findings = scanFile(root, file, allowlist);
  for (const finding of findings) {
    console.log(`${toRepoPath(root, file)}:${finding.line}: ${finding.reason}\n    ${finding.text.slice(0, 160)}`);
  }
  total += findings.length;
}

console.log(total === 0 ? `OK: ${files.length} files checked, no Polish text found.` : `${total} line(s) with Polish text.`);
process.exit(total === 0 ? 0 : 1);
