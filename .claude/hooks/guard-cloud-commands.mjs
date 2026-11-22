#!/usr/bin/env node
// PreToolUse hook (Bash, PowerShell): blocks commands that could create cost, change cloud resources, publish
// anything outside this machine, or install new tooling.
//
// WHY: ShopFlow is a learning repository with a hard zero-cost constraint (see CLAUDE.md). A deny decision with a
// reason is returned to Claude, which then explains the step instead of running it.
// Override for a deliberate, user-approved exception: set SHOPFLOW_ALLOW_CLOUD=1 in the environment that starts
// Claude Code.

import { readStdinJson, deny, allow } from "./lib/hook-io.mjs";

// A command word must start a command: beginning of input, or after ; & | ( ` $( or a newline. This keeps
// `grep "az login" docs/` or a commit message mentioning git push from being blocked.
const START = String.raw`(?:^|[;&|(\x60\n]\s*|\$\(\s*)`;

const RULES = [
  { pattern: String.raw`az\s+(?!version\b|--version\b)\S`, reason: "Azure CLI (sign-in, deployments, resource changes, Bicep CLI download)" },
  { pattern: String.raw`azd\s+(up|provision|deploy|down|auth)\b`, reason: "Azure Developer CLI provisioning or deployment" },
  { pattern: String.raw`(Connect-AzAccount|New-Az\w+|Set-Az\w+|Update-Az\w+|Remove-Az\w+|Publish-Az\w+|Invoke-AzRestMethod)\b`, reason: "Azure PowerShell changes resources" },
  { pattern: String.raw`terraform\s+(apply|destroy|import)\b`, reason: "Terraform changes real infrastructure" },
  { pattern: String.raw`pulumi\s+(up|destroy)\b`, reason: "Pulumi changes real infrastructure" },
  { pattern: String.raw`func\s+azure\b`, reason: "Azure Functions Core Tools publishes to Azure" },
  { pattern: String.raw`git\s+push\b`, reason: "pushing leaves the machine" },
  { pattern: String.raw`gh\s+(workflow\s+run|release\s+create|pr\s+(create|merge)|repo\s+create|secret\s+set|variable\s+set|api\s+.*-X\s*(POST|PUT|PATCH|DELETE))`, reason: "GitHub changes outside this machine" },
  { pattern: String.raw`docker\s+(push|login)\b`, reason: "pushing images leaves the machine" },
  { pattern: String.raw`dotnet\s+nuget\s+push\b`, reason: "publishing packages leaves the machine" },
  { pattern: String.raw`npm\s+publish\b`, reason: "publishing packages leaves the machine" },
  { pattern: String.raw`(winget|choco|scoop|brew)\s+install\b`, reason: "installing new tooling (the user declined downloads)" },
  { pattern: String.raw`(apt|apt-get)\s+install\b`, reason: "installing new tooling (the user declined downloads)" },
  { pattern: String.raw`dotnet\s+tool\s+install\s+(-g|--global)\b`, reason: "installing a global tool (use the local manifest and `dotnet tool restore`)" },
  { pattern: String.raw`npm\s+(i|install)\s+(-g|--global)\b`, reason: "installing a global npm package" },
  { pattern: String.raw`dotnet\s+ef\s+database\s+update\b`, reason: "applying migrations to a real database" },
].map((rule) => ({ ...rule, regex: new RegExp(START + rule.pattern, "i") }));

const input = await readStdinJson();
const command = input?.tool_input?.command;

if (typeof command !== "string" || process.env.SHOPFLOW_ALLOW_CLOUD === "1") {
  allow();
} else {
  const hit = RULES.find((rule) => rule.regex.test(command));
  if (hit) {
    deny(
      `Blocked by .claude/hooks/guard-cloud-commands.mjs: ${hit.reason}. ShopFlow has a zero-cost, nothing-leaves-` +
        "the-machine constraint (CLAUDE.md, 'Hard constraints'). Explain what the command would do instead of running " +
        "it, or ask the user; only they can allow an exception (SHOPFLOW_ALLOW_CLOUD=1).",
    );
  } else {
    allow();
  }
}
