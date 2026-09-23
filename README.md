# UpgradeAgent

A .NET 10 console tool that keeps a repo's NuGet packages up to date. It detects and applies version bumps deterministically, uses a Microsoft Agent Framework harness agent to fix breaking changes, checks the result with deterministic guardrails, and opens a draft PR in Azure DevOps.

See [PLAN.md](PLAN.md) for the design and milestones. **Current status: M6.** The full pipeline works, including publishing to Azure DevOps. Detection, deterministic bumps, guardrails and commits work, and a GitHub Copilot agent fixes groups that break the build. The app re-verifies every fix itself.

## Try it on the fixture

Prerequisites: .NET 10 SDK, PowerShell 7, git, and a GitHub Copilot subscription with the Copilot CLI signed in (`copilot`, then `/login`). Use `--agent none` to run without Copilot.

```pwsh
./scripts/build-fixture.ps1                  # generates artifacts/fixture/SampleRepo (LoanLedger + Fixture.Lib 1.0/1.1/2.0)
dotnet run --project src/UpgradeAgent -- plan --config fixtures/appsettings.fixture.json
dotnet run --project src/UpgradeAgent -- run  --config fixtures/appsettings.fixture.json
./scripts/reset-demo.ps1 -RepoPath artifacts/fixture/SampleRepo   # removes run worktrees, agent branches and out/run-*
dotnet run --project src/UpgradeAgent -- run  --config fixtures/appsettings.fixture.json --replay fixture        # no model needed
dotnet run --project src/UpgradeAgent -- run  --config fixtures/appsettings.fixture.json --replay fixture-cheat  # guardrails reject it
dotnet test tests/UpgradeAgent.Tests              # unit tests (seconds)
dotnet test tests/UpgradeAgent.IntegrationTests   # replays both recordings end to end (about 30 s, no model)
```

- `plan` changes nothing. It prints preflight checks and the upgrade plan, and writes `out/plan.json`.
- `run` creates branch `agent/nuget-updates-<utc>` in a worktree under `.ua-work/` beside the repo. It measures (or reuses) a baseline, then for each group: bump → restore → build → test → guardrails → commit, or revert. The repo's own working tree and branches are never touched. `--plan <file>` uses a saved plan instead of detecting, and `--only <id|family>` narrows the run.
- When a group breaks the build or tests, the Copilot agent fixes it in the worktree. Builds, tests, reads and source edits are automatic. Other actions ask you, or are declined with `--non-interactive`. Restores, package changes, git writes and anything outside the working copy are refused. The full agent log is in `out/run-*/agent/`.
- Your Copilot plan decides which models are available. The run prints the model that actually served each group.
- At the end, the run writes `out/pr-description.md` and asks to publish. Copilot must call `push_branch`, and you approve it at the prompt. In replay or `--agent none`, the app asks directly. Publishing is a dry run until the Azure DevOps integration (M6): it prints what it would push and nothing leaves the machine.
- `--record <name>` saves each agent session (its activity, a patch of its changes and its outcome) under `recordings/<name>/`. `--replay <name>` plays those back instead of calling the model, and still does the bump, build, tests, guardrails and commit for real. Replays only work at the same target commit. Recordings of real repos are gitignored because they contain your code.

## Publish to Azure DevOps

1. Create an **empty** repo in Azure DevOps, and a PAT with **Code (Read & write)**.
2. Store the PAT so it never lands in a file in this repo. Use either:
   - `dotnet user-secrets set "AzureDevOps:Pat" "<pat>" --project src/UpgradeAgent`, or
   - the environment variable `ADO_PAT`. In pipelines, `SYSTEM_ACCESSTOKEN` works, as does `az login` (Entra).
3. Copy `fixtures/appsettings.ado.example.json` to `fixtures/appsettings.ado.json` (gitignored) and fill in the org URL, project and repo.
4. One-time seed (this refuses a non-empty repo): `dotnet run --project src/UpgradeAgent -- ado-seed --config fixtures/appsettings.ado.json`
5. Run: `dotnet run --project src/UpgradeAgent -- run --config fixtures/appsettings.ado.json --ado`
   - The credential and repo are checked before any work starts.
   - At the end, the agent calls `push_branch` and you approve it.
   - The branch is pushed, and a **draft** PR is opened with the `agent-generated` label.
6. Reset: `./scripts/reset-demo.ps1 -RepoPath artifacts/fixture/SampleRepo -Ado -Config fixtures/appsettings.ado.json`
   - This abandons the labelled agent PRs and deletes the `agent/nuget-updates-*` branches, after listing them and asking.
   - The default branch is never touched.

## Point it at another repo

Create a config file (relative paths resolve against the config file's folder):

```json
{
  "Target": { "RepoPath": "../interest_accrual", "Solution": "InterestAccrual.sln" },
  "Policy": { "TargetOverrides": { "Some.Package": "3.2.0" } }
}
```

Then run `dotnet run --project src/UpgradeAgent -- plan --config <file>`. Settings layer as follows: `appsettings.json`, then `--config`, then user secrets, then `UPGRADEAGENT_` environment variables (e.g. `UPGRADEAGENT_Target__RepoPath`).
