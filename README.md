# UpgradeAgent

A .NET 10 console tool that keeps a repo's NuGet packages up to date. It detects and applies version bumps deterministically, uses a Microsoft Agent Framework harness agent to fix breaking changes, checks the result with deterministic guardrails, and opens a draft PR in Azure DevOps.

See [PLAN.md](PLAN.md) for the design and milestones. **Current status: M5 (dry-run publishing).** Detection, deterministic bumps, guardrails and commits work, and a GitHub Copilot agent fixes groups that break the build. The app re-verifies every fix itself.

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

## Point it at another repo

Create a config file (relative paths resolve against the config file's folder):

```json
{
  "Target": { "RepoPath": "../interest_accrual", "Solution": "InterestAccrual.sln" },
  "Policy": { "TargetOverrides": { "Some.Package": "3.2.0" } }
}
```

Then run `dotnet run --project src/UpgradeAgent -- plan --config <file>`. Settings layer as follows: `appsettings.json`, then `--config`, then user secrets, then `UPGRADEAGENT_` environment variables (e.g. `UPGRADEAGENT_Target__RepoPath`).
