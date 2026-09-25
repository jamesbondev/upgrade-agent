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
| `src/RepoKit` | Standalone library, no packages: processes, git, clones of a repo into a throwaway workspace. No LLM concepts. |
| `src/RepoKit.AzureDevOps` | Standalone library: Azure DevOps credentials, repo addresses and draft PRs (REST). Doesn't reference RepoKit. |
| `src/UpgradeAgent` | App: NuGet upgrades with an agent fixing breaking changes, published as a draft PR in Azure DevOps. |
| `src/ReadmeChecker` | App: finds READMEs that no longer match their repo, and opens draft PRs that fix them. |
| `samples/HelloAgent` | The smallest runnable use of AgentHarness. `--scripted` needs no model. |
| `tests/` | xUnit. `*.IntegrationTests` replay recorded agent sessions end to end. |
| `fixtures/` | Source for the sample repo and package the integration tests run against. |
| `PLAN.md` | UpgradeAgent's design and milestones. |

## Commands

```sh
dotnet build UpgradeAgent.slnx
dotnet test tests/AgentHarness.Tests
dotnet test tests/RepoKit.Tests
dotnet test tests/RepoKit.AzureDevOps.Tests
dotnet test tests/ReadmeChecker.Tests
dotnet test tests/UpgradeAgent.Tests
dotnet test tests/UpgradeAgent.IntegrationTests   # about 30 s, no model
```

The build treats warnings as errors and enforces the `.editorconfig` style. A change is done when the build is clean
and every test project passes.

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
- **One `CopilotBackend` per directory in use at the same time.** A backend restarts its runtime when a session
  starts in another directory, which breaks sessions still running in the old one.
- **Treat every cloned repo as untrusted.** Clone through `RepoWorkspace`, which turns symlinks into plain files
  (policy path checks don't follow links). Validate anything the agent cites against `git ls-files`, not the file
  system. Parse its files defensively: XML with DTDs prohibited, and a malformed file is skipped, not fatal.
- **One repo's failure never stops a multi-repo run.** Catch per repo and report it as that repo's result; only
  configuration, sign-in and "Copilot isn't ready" errors end the run.
- **Claims about code need machine-checkable proof.** When an agent says the code contradicts a document, require a
  snippet copied from a cited source or config file that the app then finds in that file, or a term that `git grep`
  can't find; reject anything else. See `ReadmeChecker/Agent/EvidenceChecks.cs`.
- **Publishing is gated.** An agent's write session may change only the files the task owns (an exact path, not an
  extension). After the script's checks, a person approves each push unless they pass `--yes`, pull requests are
  drafts, and agent text in a PR is escaped with `@` mentions defused. Use a new branch per run, and decide whether
  to skip from the PR history (open or recent), not from whether a branch exists.
- **Credentials reach git only through the environment** (`GitAuth.HeaderEnvironment`), never in a URL or in
  `.git/config`, where the agent could read them. Hide credential env vars from the agent's environment.
- **Known duplication:** `src/UpgradeAgent/Infrastructure` and parts of `Publishing` are copies of what RepoKit now
  holds. UpgradeAgent moves onto RepoKit in a later milestone; until then, fix a bug in both places.

## Plans

Put planning notes in `.planning/`, one folder per plan named `YYYY-MM-DD-short-desc` (for example
`.planning/2026-09-25-automation-ideas/`). The folder is gitignored, so plans stay local. `PLAN.md` is the
exception: UpgradeAgent's committed design.

## Git workflow

- **Work in a worktree.** Before changing tracked files, create a worktree on a new branch under
  `.claude/worktrees/<short-desc>` (gitignored) and do all the work there, because several agents often work in this
  repo at once. Skip this only when the user says to work on the current branch.
- **Ignored files stay in the main checkout.** A worktree has no `.planning/`, `fixtures/appsettings.ado.json` or
  real-repo recordings. Read and write plans in the main checkout's `.planning/`, and copy local settings in if a run
  needs them.
- **Remove the worktree after a push.** When the user asks for a push and it succeeds, remove the worktree. First `cd`
  to the main checkout and check `pwd`: removing the directory the shell is in kills the session.

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
