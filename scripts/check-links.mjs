#!/usr/bin/env node
// Checks relative links in Markdown files: the target file or directory exists, and a #anchor matches a heading
// (GitHub slug rules). External links (http, mailto) are not fetched: the check works offline.
//
// Usage: node scripts/check-links.mjs [file.md ...]     (no arguments: every Markdown file in the repository)

import { readFileSync, readdirSync, existsSync, statSync } from "node:fs";
import { resolve, dirname, join, relative, isAbsolute, sep } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const SKIPPED_DIRECTORIES = new Set([".git", "bin", "obj", "node_modules", "artifacts", "TestResults"]);

function* markdownFiles(directory) {
  for (const name of readdirSync(directory)) {
    const fullPath = join(directory, name);
    if (statSync(fullPath).isDirectory()) {
      if (!SKIPPED_DIRECTORIES.has(name)) yield* markdownFiles(fullPath);
    } else if (name.endsWith(".md")) {
      yield fullPath;
    }
  }
}

const withoutCode = (text) => text.replace(/```[\s\S]*?```/g, "").replace(/`[^`\n]*`/g, "");

const slugCache = new Map();
function headingSlugs(file) {
  if (slugCache.has(file)) return slugCache.get(file);
  const slugs = new Set();
  const seen = new Map();
  for (const line of readFileSync(file, "utf8").replace(/```[\s\S]*?```/g, "").split(/\r?\n/)) {
    const match = /^#{1,6}\s+(.*?)\s*#*\s*$/.exec(line);
    if (!match) continue;
    let slug = match[1].toLowerCase().replace(/[^\p{L}\p{N}\s_-]/gu, "").replace(/\s/g, "-");
    const count = seen.get(slug);
    seen.set(slug, (count ?? -1) + 1);
    if (count !== undefined) slug = `${slug}-${count + 1}`;
    slugs.add(slug);
  }
  slugCache.set(file, slugs);
  return slugs;
}

const files = process.argv.length > 2
  ? process.argv.slice(2).map((p) => (isAbsolute(p) ? p : resolve(root, p))).filter((p) => p.endsWith(".md") && existsSync(p))
  : [...markdownFiles(root)];

let checked = 0;
let problems = 0;
for (const file of files) {
  const text = withoutCode(readFileSync(file, "utf8"));
  for (const [, target] of text.matchAll(/\[[^\]]*\]\(([^)\s]+)\)/g)) {
    if (/^(https?:|mailto:)/.test(target) || target === "#") continue;
    checked++;
    const [path, anchor] = target.split("#");
    const resolved = path ? resolve(dirname(file), decodeURIComponent(path)) : file;
    const where = relative(root, file).split(sep).join("/");
    if (!existsSync(resolved)) {
      console.log(`MISSING  ${where} -> ${target}`);
      problems++;
    } else if (anchor && resolved.endsWith(".md") && !headingSlugs(resolved).has(decodeURIComponent(anchor).toLowerCase())) {
      console.log(`ANCHOR   ${where} -> ${target}`);
      problems++;
    }
  }
}

console.log(`Checked ${checked} links in ${files.length} files, ${problems} problem(s).`);
process.exit(problems === 0 ? 0 : 1);
