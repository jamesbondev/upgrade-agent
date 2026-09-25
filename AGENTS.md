# AGENTS.md

Rules for anyone, human or agent, working in this repo. `CLAUDE.md` imports this file, so Claude Code and Copilot
read the same rules. Keep it short: rules and non-obvious facts only. How things work belongs in the READMEs.

## What this repo is

A C# toolkit for building apps that run GitHub Copilot as an agent over our team's repos, plus the apps themselves.
The target flow for every app: find the repos we own → clone → decide whether something needs doing → run an agent
to do it → verify → open a PR.

| Path | What it is |
|---|---|
| `src/AgentHarness` | Standalone library: Copilot sessions, tool policy, budgets, events, structured replies. See its README. |
| `src/UpgradeAgent` | App: NuGet upgrades with an agent fixing breaking changes, published as a draft PR in Azure DevOps. |
| `samples/HelloAgent` | The smallest runnable use of AgentHarness. `--scripted` needs no model. |
| `tests/` | xUnit. `*.IntegrationTests` replay recorded agent sessions end to end. |
| `fixtures/` | Source for the sample repo and package the integration tests run against. |
| `PLAN.md` | UpgradeAgent's design and milestones. |

## Commands

```sh
dotnet build UpgradeAgent.slnx
dotnet test tests/AgentHarness.Tests
dotnet test tests/UpgradeAgent.Tests
dotnet test tests/UpgradeAgent.IntegrationTests   # about 30 s, no model
```

The build treats warnings as errors and enforces the `.editorconfig` style. A change is done when the build is clean
and all three test projects pass.

## Architecture rules

- **Components are independent.** Someone must be able to copy one library folder into their solution and use it
  without the others. A library never has a `ProjectReference` to a sibling library, and its csproj builds with or
  without central package management (see `AgentHarness.csproj`).
- **AgentHarness knows only a working directory.** It has no idea of repos, git hosts, PRs, Azure DevOps or any app.
  If a feature needs one of those, it belongs in the app or in another library, passed in as a tool, policy or
  observer.
- **Repo plumbing doesn't know about LLMs.** Discovering repos, cloning, branching, committing, pushing and opening
  PRs belong in their own library, which must not reference AgentHarness. Apps compose the two.
- **The model is one untrusted step.** Decide whether work is needed deterministically where you can, and when you
  ask a model, use a read-only session and `AskAsync<T>`. Verify every agent change with plain code (build, tests,
  diff guardrails) before committing. The agent's summary is a claim, never a result.
- **Tests never call a model.** Use `ScriptedBackend` for sessions, and `--record`/`--replay` for app runs.

## Code rules

- **No comments in `.cs` files.** None: no `//`, no `/* */`, no `///` XML docs, no commented-out code. Say it with
  names, small methods and tests. Anything a reader truly needs goes in the component's README. The only exception
  is `fixtures/`: it stands in for someone else's repo, and changing `fixtures/SampleRepo` changes the fixture
  commit, which invalidates the recordings.
- Match the existing style: file-scoped namespaces, primary constructors, `_camelCase` private fields, braces
  always, `internal` unless something must be public.

## Before every commit

Before any commit, review what the session changed and ask: is there a new pattern, convention, gotcha, command or
constraint that another session, or another person using this repo, must know? If so, update this file in the same
commit. Add or correct rules; remove ones that are no longer true. If nothing qualifies, leave it alone.
