// Shared input/output helpers for the Claude Code hooks in .claude/hooks.
// Protocol: the hook gets a JSON object on stdin and answers with JSON on stdout (exit code 0).

import { resolve, dirname, relative, isAbsolute, sep } from "node:path";
import { fileURLToPath } from "node:url";

export const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..", "..");

// Repository-relative path with forward slashes, or null for a path outside the repository.
// WHY: on Windows relative() between two drives returns an absolute path, not one starting with "..".
export function repositoryPath(absolutePath) {
  const path = relative(repositoryRoot, absolutePath);
  return path.startsWith("..") || isAbsolute(path) ? null : path.split(sep).join("/");
}

export async function readStdinJson() {
  let text = "";
  for await (const chunk of process.stdin) text += chunk;
  try {
    return JSON.parse(text);
  } catch {
    return null; // Unknown input: the hooks fail open rather than block every tool call.
  }
}

// PreToolUse: let the call through (the normal permission flow still applies).
export function allow() {
  process.exit(0);
}

// PreToolUse: refuse the call; the reason is shown to Claude.
export function deny(reason) {
  process.stdout.write(
    JSON.stringify({
      hookSpecificOutput: { hookEventName: "PreToolUse", permissionDecision: "deny", permissionDecisionReason: reason },
    }),
  );
  process.exit(0);
}

// PostToolUse: the edit already happened; ask Claude to fix it.
export function block(reason) {
  process.stdout.write(JSON.stringify({ decision: "block", reason }));
  process.exit(0);
}

// The path an editing tool writes to (Write, Edit, MultiEdit: file_path; NotebookEdit: notebook_path).
export function editedPath(input) {
  const path = input?.tool_input?.file_path ?? input?.tool_input?.notebook_path;
  return typeof path === "string" ? resolve(input?.cwd ?? repositoryRoot, path) : null;
}
