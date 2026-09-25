#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Removes everything UpgradeAgent runs left behind for a target repo, so the demo can run again.

.DESCRIPTION
    Removes run worktrees under the work root (default: .ua-work beside the repo), deletes local
    agent/nuget-updates-* branches and clears out/run-* folders. The repo's own branches and working
    tree are never touched. The baseline cache is kept (it keeps rehearsals fast) unless -ClearBaseline.

    Supports -WhatIf.

.EXAMPLE
    ./scripts/reset-demo.ps1 -RepoPath artifacts/fixture/SampleRepo

.EXAMPLE
    ./scripts/reset-demo.ps1 -RepoPath artifacts/fixture/SampleRepo -Ado -Config fixtures/appsettings.ado.json
    Also abandons UpgradeAgent's labelled draft PRs and deletes its agent/nuget-updates-* branches in Azure DevOps
    (lists them and asks first). The remote's default branch is never touched.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $RepoPath,
    [string] $WorkRoot,
    [string] $OutputDirectory = (Join-Path (Get-Location) 'out'),
    [switch] $ClearBaseline,
    [switch] $Ado,
    [string] $Config
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'Common.psm1') -Force

$repo = (Resolve-Path $RepoPath).Path.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
if (-not $WorkRoot) { $WorkRoot = Join-Path (Split-Path $repo -Parent) '.ua-work' }
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)

# Build servers and MSBuild nodes hold file locks on Windows; worktree removal fails without this.
if ($PSCmdlet.ShouldProcess('dotnet build servers', 'shut down')) {
    dotnet build-server shutdown | Out-Null
}

$worktrees = Invoke-Checked git @('-C', $repo, 'worktree', 'list', '--porcelain') -PassThru |
    Where-Object { $_ -like 'worktree *' } |
    ForEach-Object { [IO.Path]::GetFullPath(($_ -replace '^worktree ', '')) } |
    Where-Object { Test-PathInside -Path $_ -Root $WorkRoot }

foreach ($worktree in $worktrees) {
    if ($PSCmdlet.ShouldProcess($worktree, 'remove worktree')) {
        Invoke-Checked git @('-C', $repo, 'worktree', 'remove', '--force', $worktree)
    }
}
Invoke-Checked git @('-C', $repo, 'worktree', 'prune')

$branches = Invoke-Checked git @('-C', $repo, 'for-each-ref', '--format=%(refname:short)', 'refs/heads/agent/nuget-updates-*') -PassThru
foreach ($branch in $branches) {
    if ($PSCmdlet.ShouldProcess($branch, 'delete local branch')) {
        Invoke-Checked git @('-C', $repo, 'branch', '-D', '-q', $branch)
    }
}

if (Test-Path $WorkRoot) {
    Get-ChildItem $WorkRoot -Directory |
        Where-Object { $ClearBaseline -or $_.Name -ne 'baseline' } |
        ForEach-Object { if ($PSCmdlet.ShouldProcess($_.FullName, 'delete')) { Remove-Item $_.FullName -Recurse -Force } }
}

if (Test-Path $OutputDirectory) {
    Get-ChildItem $OutputDirectory -Directory -Filter 'run-*' |
        ForEach-Object { if ($PSCmdlet.ShouldProcess($_.FullName, 'delete')) { Remove-Item $_.FullName -Recurse -Force } }
}

if ($Ado) {
    if (-not $Config) { throw '-Ado needs -Config <file> with the AzureDevOps settings.' }
    $project = Join-Path $PSScriptRoot '..' 'src' 'UpgradeAgent'
    # Called directly, not through Invoke-Checked: ado-cleanup asks for confirmation, so it needs the real console.
    dotnet run --project $project -- ado-cleanup --config (Resolve-Path $Config).Path
    if ($LASTEXITCODE -ne 0) { throw "ado-cleanup failed with exit code $LASTEXITCODE." }
}

Write-Host "Reset complete: $(@($worktrees).Count) worktree(s), $(@($branches).Count) branch(es) removed."
