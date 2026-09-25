# UpgradeAgent

A .NET 10 console tool that keeps a repo's NuGet packages up to date. It detects and applies version bumps deterministically, uses a Microsoft Agent Framework harness agent to fix breaking changes, checks the result with deterministic guardrails, and opens a draft PR in Azure DevOps.

The Copilot calls and the agentic tool calling live in **[AgentHarness](src/AgentHarness/README.md)**, a standalone library you can copy into another solution. Start with `dotnet run --project samples/HelloAgent -- --scripted` (no Copilot login needed), and see [docs/agent-harness-tour.md](docs/agent-harness-tour.md) for a walkthrough.

See [PLAN.md](PLAN.md) for the design and milestones. **Current status: M8.** The full pipeline works, including publishing to Azure DevOps, and the codebase has been through a maintainability cleanup (see [How it works with the LLM](#how-it-works-with-the-llm)).

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
- When a group breaks the build or tests, the Copilot agent fixes it in the worktree. Builds, tests, reads and source edits are automatic. Edits to project and build files ask you, or are declined with `--non-interactive`. Everything else (restores, package changes, git writes, network tools, other commands, anything outside the working copy) is refused with a reason the agent can act on. The full agent log is in `out/run-*/agent/`.
- The agent is stopped early when it isn't getting anywhere: more than `Agent:MaxRefusalsPerGroup` (5) refused actions, or `Agent:MaxBuildsWithoutProgress` (3) builds in a row without fewer errors. Besides that, each group has 10 minutes and 80 tool calls.
- Bumps that are too large for the agent are left for a human. A major step crossing more than `Policy:MaxMajorJump` (2) major versions is marked manual in the plan (the step to the latest minor still runs). A bump that leaves more than `Agent:MaxErrorsForAgent` (50) build errors is rejected without calling the agent. Use `Policy:TargetOverrides` to move a far-behind package in stages. Set any of these to 0 to disable it.
- Your Copilot plan decides which models are available. The run prints the model that actually served each group.
- At the end, the run writes `out/pr-description.md` and asks to publish. Copilot must call `push_branch`, and you approve it at the prompt. In replay or `--agent none`, the app asks directly. Publishing is a dry run until the Azure DevOps integration (M6): it prints what it would push and nothing leaves the machine.
- `--record <name>` saves each agent session (its activity, a patch of its changes and its outcome) under `recordings/<name>/`. `--replay <name>` plays those back instead of calling the model, and still does the bump, build, tests, guardrails and commit for real. Replays only work at the same target commit, and use the recording's plan, so `--replay` can't be combined with `--plan`, `--agent` or `--record`. Recordings of real repos are gitignored because they contain your code.

## How it works with the LLM

The model is one untrusted step in a deterministic pipeline. Everything around it is plain code that can be unit-tested.

```
detect → plan (policy rules, families, TFM check) → per group: bump → restore → build → test
                                                                      │ broken?
                                                                      ▼
            AgentFixRunner  ── prompts (FixPrompts: notes inlined as untrusted data, current errors)
                            ── AgentHarness session: every tool call through the policy
                               (CommandPolicy on WorkspacePolicy → notes-first → operator)
                            ── limits (time, tool calls, refusals) + a stop rule for builds without progress
                            ── SessionMonitor: harness events → activity (console, log, recordings)
                            ── AskAsync<GroupSummary> (JSON schema generated from the type)
                            ── IAgentBackend: CopilotBackend today; ScriptedBackend in tests
                                                                      │
                                                                      ▼
      rebuild → retest → guardrails (one IGuardrail per check) → commit, or revert
                                                                      │
      push_branch (approval-required tool that re-checks the ledger) → draft PR (agent text escaped, marked unverified)
```

- **Grounding:** the app finds the migration notes and pastes them in, with the current build errors. Notes too large to paste must be read before any edit.
- **Least privilege:** reads, builds and source edits are automatic; project-file edits need a human; everything else is refused with a reason the model can act on. The agent's environment has no secrets.
- **Budgets and stop rules:** time (paused while a human decides), tool calls, refusals, and builds that don't reduce the error count.
- **Never trusted:** the app rebuilds, retests and runs the guardrails itself; the agent's summary is only compared with the diff.
- **Observable:** typed activity events go to the console, a per-group log and recordings; OpenTelemetry spans and metrics (`AgentHarness`); `--verbose` traces every external command.
- **Reproducible:** `--record`/`--replay` capture sessions as events plus a patch, so demos and tests run without a model.

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

## Corporate setups: private feeds and git hooks

**Custom `nuget.config` / private feeds**
- Runs happen in a git worktree, which only contains **committed** files. The run preflight fails if a `nuget.config`, `Directory.*.props/targets` or `global.json` exists in the repo but isn't committed. Commit it, or move it to the folder **above** the repo (the repo and `.ua-work/` both inherit from there), or put the feeds in your user-level NuGet config.
- Runs never prompt, so authenticate private feeds once beforehand (for example `dotnet restore --interactive` for Azure Artifacts). The credential provider caches the token, and the app's restores reuse it.
- The agent never restores. It's refused `dotnet restore`, `--no-restore` is required, and feed variables (`VSS_NUGET_*`) are removed from its environment. It's also refused access to credential files: `nuget.config`, `.env`, `secrets.json`, `*.pfx/.snk/.pem/.key`. This is a usability layer, not a sandbox. Keep feed passwords out of committed files (use the credential provider or `%ENV%` references).
- The fixture's own `nuget.config` uses nuget.org. If nuget.org is only reachable through a proxy, NuGet's proxy settings still apply. If it's blocked outright, the fixture can't restore.

**Git hooks (ggshield, pre-commit, …)**
- Agent commits run the repository's hooks, including a global `core.hooksPath` such as ggshield's. A hook that fails **rejects the group** and shows its output. A hook that rewrites files also rejects the group, because only the tree the guardrails verified may be committed. Pushes run pre-push hooks too.
- `Target:RunGitHooks: false` exists for repos whose hooks can't run unattended. Prefer fixing the hook.
- Hook tools keep their own keys (for example `GITGUARDIAN_API_KEY`). The app's git commands can use them, but the agent's environment drops anything named like a key or token.
- The generated fixture repo is synthetic and local, so it opts out of global hooks (`core.hooksPath=.git/no-hooks`). The unit-test repos do the same.

## Point it at another repo

Create a config file (relative paths resolve against the config file's folder):

```json
{
  "Target": { "RepoPath": "../interest_accrual", "Solution": "InterestAccrual.sln" },
  "Policy": { "TargetOverrides": { "Some.Package": "3.2.0" } }
}
```

Then run `dotnet run --project src/UpgradeAgent -- plan --config <file>`. Settings layer as follows: `appsettings.json`, then `--config`, then user secrets, then `UPGRADEAGENT_` environment variables (e.g. `UPGRADEAGENT_Target__RepoPath`).
