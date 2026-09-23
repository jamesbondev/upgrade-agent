#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Creates the development fixture: a fresh git repo for LoanLedger under artifacts/fixture/SampleRepo.

.DESCRIPTION
    The generated repo's first commit is deterministic (fixed identity, dates and committed
    nupkgs), so replay recordings made on one machine match on another.

    -Repack rebuilds Fixture.Lib 1.0.0 / 1.1.0 / 2.0.0 into fixtures/SampleRepo/.feed. Do this only
    when the fixture package changes; it alters the fixture commit and invalidates recordings.
#>
[CmdletBinding()]
param(
    [switch] $Repack,
    [string] $OutputRoot = (Join-Path $PSScriptRoot '..' 'artifacts' 'fixture')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Invoke-Checked {
    param([string] $File, [string[]] $Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "'$File $($Arguments -join ' ')' failed with exit code $LASTEXITCODE." }
}

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$source = Join-Path $root 'fixtures' 'SampleRepo'
$sourceFeed = Join-Path $source '.feed'
$library = Join-Path $root 'fixtures' 'Fixture.Lib' 'Fixture.Lib.csproj'
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
$repo = Join-Path $OutputRoot 'SampleRepo'

if ($Repack -or -not (Test-Path (Join-Path $sourceFeed 'fixture.lib.2.0.0.nupkg'))) {
    Write-Host 'Packing Fixture.Lib 1.0.0, 1.1.0 and 2.0.0...'
    Remove-Item $sourceFeed -Recurse -Force -ErrorAction SilentlyContinue
    foreach ($api in '1.0', '1.1', '2.0') {
        Invoke-Checked dotnet @('build', $library, '-c', 'Release', "-p:FixtureApi=$api", '--no-incremental', '-nologo', '-v', 'q')
        Invoke-Checked dotnet @('pack', $library, '-c', 'Release', "-p:FixtureApi=$api", '--no-build', '-o', $sourceFeed, '-nologo', '-v', 'q')
    }
    Get-ChildItem $sourceFeed -Filter '*.nupkg' | Rename-Item -NewName { $_.Name.ToLowerInvariant() } -ErrorAction SilentlyContinue
}

# Repacking reuses version numbers, so never let restore serve a stale Fixture.Lib from the global cache.
$globalPackages = (dotnet nuget locals global-packages --list) -replace '^global-packages:\s*', ''
Remove-Item (Join-Path $globalPackages.Trim() 'fixture.lib') -Recurse -Force -ErrorAction SilentlyContinue

if (Test-Path $OutputRoot) {
    Write-Host "Removing previous fixture at $OutputRoot..."
    dotnet build-server shutdown | Out-Null
    Remove-Item $OutputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
Copy-Item -Path $source -Destination $repo -Recurse
Get-ChildItem $repo -Directory -Recurse -Include 'bin', 'obj', 'TestResults' | Remove-Item -Recurse -Force

Write-Host "Creating git repo at $repo..."
$env:GIT_AUTHOR_NAME = 'UpgradeAgent Fixture'
$env:GIT_AUTHOR_EMAIL = 'fixture@example.invalid'
$env:GIT_AUTHOR_DATE = '2026-01-01T00:00:00Z'
$env:GIT_COMMITTER_NAME = $env:GIT_AUTHOR_NAME
$env:GIT_COMMITTER_EMAIL = $env:GIT_AUTHOR_EMAIL
$env:GIT_COMMITTER_DATE = $env:GIT_AUTHOR_DATE
try {
    Invoke-Checked git @('-C', $repo, 'init', '-q', '-b', 'main')
    Invoke-Checked git @('-C', $repo, '-c', 'core.autocrlf=false', 'add', '-A')
    Invoke-Checked git @('-C', $repo, '-c', 'commit.gpgsign=false', 'commit', '-q', '-m', 'Initial commit')
}
finally {
    'GIT_AUTHOR_NAME', 'GIT_AUTHOR_EMAIL', 'GIT_AUTHOR_DATE', 'GIT_COMMITTER_NAME', 'GIT_COMMITTER_EMAIL', 'GIT_COMMITTER_DATE' |
        ForEach-Object { Remove-Item "env:$_" -ErrorAction SilentlyContinue }
}

Write-Host 'Verifying baseline build and tests...'
Invoke-Checked dotnet @('test', (Join-Path $repo 'LoanLedger.slnx'), '-nologo', '-v', 'q')

$commit = (git -C $repo rev-parse HEAD).Trim()
Write-Host "Fixture ready: $repo (commit $commit)"
