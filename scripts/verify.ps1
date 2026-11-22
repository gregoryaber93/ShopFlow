<#
.SYNOPSIS
    Local verification: the cloud-free part of .github/workflows/ci.yml (job build-and-test), in the same order,
    plus the repository's English and link checks. Nothing here signs in to Azure or leaves the machine.

.PARAMETER Quick
    Skip Pact and the EF Core model check.

.PARAMETER SkipFormat
    Skip `dotnet format whitespace --verify-no-changes`.

.PARAMETER Configuration
    Build configuration. CI builds Release; the default here is Debug to reuse the IDE's build output.

.EXAMPLE
    ./scripts/verify.ps1 -Quick
#>
[CmdletBinding()]
param(
    [switch] $Quick,
    [switch] $SkipFormat,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:PACT_DO_NOT_TRACK = 'true'

$script:results = New-Object System.Collections.Generic.List[string]
$script:failed = $false

function Invoke-Step {
    param([string] $Name, [string[]] $Command)
    Write-Host ''
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Command[0] $Command[1..($Command.Length - 1)]
    if ($LASTEXITCODE -eq 0) {
        $script:results.Add("PASS  $Name")
    }
    else {
        $script:results.Add("FAIL  $Name (exit code $LASTEXITCODE)")
        $script:failed = $true
    }
}

function Write-Summary {
    Write-Host ''
    Write-Host '==> Summary' -ForegroundColor Cyan
    foreach ($line in $script:results) {
        if ($line.StartsWith('FAIL')) { Write-Host $line -ForegroundColor Red }
        elseif ($line.StartsWith('SKIP')) { Write-Host $line -ForegroundColor Yellow }
        else { Write-Host $line -ForegroundColor Green }
    }
    if ($script:failed) { Write-Host 'Verification FAILED.' -ForegroundColor Red }
    else { Write-Host 'Verification passed.' -ForegroundColor Green }
}

function Get-PactHash {
    $files = Get-ChildItem -Path 'tests/ShopFlow.Pact.Tests/pacts' -Filter '*.json' -ErrorAction SilentlyContinue | Sort-Object Name
    if (-not $files) { return '' }
    ($files | ForEach-Object { (Get-FileHash -Path $_.FullName -Algorithm SHA256).Hash }) -join ','
}

Invoke-Step 'restore' @('dotnet', 'restore', 'ShopFlow.slnx')

if ($SkipFormat) {
    $script:results.Add('SKIP  format (-SkipFormat)')
}
else {
    Invoke-Step 'format' @('dotnet', 'format', 'whitespace', 'ShopFlow.slnx', '--verify-no-changes', '--verbosity', 'minimal')
}

Invoke-Step 'build' @('dotnet', 'build', 'ShopFlow.slnx', '--no-restore', '--configuration', $Configuration)
if ($script:failed) {
    # Tests on a broken build only add noise.
    Write-Summary
    exit 1
}

Invoke-Step 'tests' @('dotnet', 'test', '--solution', 'ShopFlow.slnx', '--no-build', '--configuration', $Configuration,
    '--filter-not-namespace', 'ShopFlow.Pact.Tests*', '--ignore-exit-code', '8')

if ($Quick) {
    $script:results.Add('SKIP  pact (-Quick)')
    $script:results.Add('SKIP  ef model (-Quick)')
}
else {
    # WHY: CI fails when the consumer tests change a committed pact. Comparing hashes before and after works with or
    # without git.
    $before = Get-PactHash
    Invoke-Step 'pact consumers' @('dotnet', 'test', '--project', 'tests/ShopFlow.Pact.Tests', '--no-build',
        '--configuration', $Configuration, '--filter-namespace', 'ShopFlow.Pact.Tests.Consumers')
    if ($before -eq (Get-PactHash)) {
        $script:results.Add('PASS  pact files unchanged')
    }
    else {
        $script:results.Add('FAIL  pact files unchanged (consumer tests regenerated different pacts: review and keep them with the change)')
        $script:failed = $true
    }
    Invoke-Step 'pact providers' @('dotnet', 'test', '--project', 'tests/ShopFlow.Pact.Tests', '--no-build',
        '--configuration', $Configuration, '--filter-namespace', 'ShopFlow.Pact.Tests.Providers')

    Invoke-Step 'tool restore' @('dotnet', 'tool', 'restore')
    Invoke-Step 'ef model matches migrations' @('dotnet', 'ef', 'migrations', 'has-pending-model-changes', '--no-build',
        '--configuration', $Configuration, '--project', 'src/Services/Inventory/ShopFlow.Inventory.Infrastructure')
}

if (Get-Command node -ErrorAction SilentlyContinue) {
    Invoke-Step 'english' @('node', 'scripts/check-english.mjs')
    Invoke-Step 'links' @('node', 'scripts/check-links.mjs')
}
else {
    $script:results.Add('SKIP  english, links (node not found)')
}

Write-Summary
if ($script:failed) { exit 1 } else { exit 0 }
