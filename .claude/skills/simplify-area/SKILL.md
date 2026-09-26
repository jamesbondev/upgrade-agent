---
name: simplify-area
description: Point at a file, folder or symbol and simplify it holistically, mapping its callers and callees first so the fix lands at the right depth and nothing outside the target is left inconsistent
allowed-tools: Bash, Read, Write, Edit, Glob, Grep, Agent, AskUserQuestion
---

# Simplify Area

Simplify a named area of the codebase without changing its behaviour. The target is a starting point, not a boundary: the review covers the target, what calls it, what it calls, and the tests that pin it, so a simplification can move responsibility to where it belongs rather than being confined to the file it was pointed at. It never crosses a component boundary, though (see below).

This is quality work, not bug hunting. A defect found on the way is fixed on the way (there is no such thing as a pre-existing bug), but the angles below do not look for them. Use `/code-review` for that.

## Arguments

`<target>` is one of:

- a file path: `src/ReadmeChecker/Agent/EvidenceChecks.cs`
- a folder: `src/UpgradeAgent/Guardrails/`
- a symbol: `GuardrailRunner` or `EvidenceChecks.QuoteProblem`

`--apply` skips the approval pause after the plan and applies every confirmed finding. Without it the run stops at the plan and asks.

## Models

Run the session itself on Fable 5.1: the driver holds the map, the plan, the edits and the call on whether a failing test pinned behaviour or implementation, and its mistakes become commits. Every agent this skill launches is passed `model: opus`. The reviewers only need recall, since the verifier is the precision gate; the verifier and the plan reviewer must be a different model from the driver so the plan is not marking its own work. Never use Haiku for any step here, including the caller map, which needs the same-named-class judgement described in Step 1.

## What is not a simplification here

Read these before the angles run, and hand them to every agent. Each is a shape that looks like over-engineering or duplication and is a deliberate decision. Most come from `AGENTS.md`, which wins if the two disagree.

- **Components stay independent.** `AgentHarness`, `RepoKit` and `RepoKit.AzureDevOps` never gain a `ProjectReference` to a sibling library, and an app's code is never moved into a library to share it. Code duplicated across component boundaries is deliberate. That includes the known duplication between `src/UpgradeAgent/Infrastructure` (and parts of `Publishing`) and RepoKit, which a later milestone removes; it is not a finding here. If a fix touches one copy, apply it to the other too.
- **Layer knowledge stays put.** AgentHarness knows only a working directory: no repo, git host, PR or app concept moves into it, however convenient. RepoKit knows nothing about LLMs.
- **A library's `public` surface is its API.** Libraries are meant to be copied into other solutions, so a `public` member with no caller in this repo is not dead code. `internal` is not widened to `public` to make a test or a caller easier; tests use `InternalsVisibleTo`.
- **Library csproj files build with or without central package management** (see `AgentHarness.csproj`). Don't "tidy" them to match the apps.
- **Security and trust checks stay, even when they look redundant.** Symlink flattening in `RepoWorkspace`, validating agent citations against `git ls-files`, XML parsing with DTDs prohibited, skipping a malformed file instead of failing, evidence checks on agent claims (`EvidenceChecks`), credentials only through `GitAuth.HeaderEnvironment` with credential env vars hidden from the agent, the publishing gates (exact owned paths, approval unless `--yes`, draft PRs, escaped agent text with `@` mentions defused). A second check of the same thing is defence in depth; never merge, loosen or remove one.
- **Per-repo catch blocks in multi-repo runs are not swallowed exceptions.** One repo's failure is reported as that repo's result by design.
- **The model seam stays.** `IAgentBackend` exists so `ScriptedBackend` can replace the model in tests. Read-only sessions and `AskAsync<T>` for decisions are the rule, not ceremony.
- **One `CopilotBackend` per directory in use at a time.** Never share, pool or reuse a backend across directories for efficiency.
- **`fixtures/` is out of scope.** Changing `fixtures/SampleRepo` changes the fixture commit and invalidates the recordings. A finding that would need a recording re-made (which needs a real model) is DEFERRED, not applied.
- Result types for expected failures are not collapsed into exceptions, and exceptions for unexpected failures are not wrapped into results.
- Test gaps are not findings. This skill does not write tests for uncovered paths.
- A method that is one linear concern end to end does not get split because it is long. Extraction needs a real seam between distinct responsibilities, such as deciding whether to proceed versus doing the mechanical work once decided.
- How a sibling file does it is not evidence either way. Judge the target on its own responsibilities. Mention a sibling only to flag that it has the same problem and should get the same treatment later.

## Steps

### 0. Worktree and folder

Unless the user said to work on the current branch, create a worktree and branch from `main` before anything else, from the main checkout: `git worktree add .claude/worktrees/simplify-<slug> -b refactor/simplify-<slug> main`. Work there for the whole run.

Planning notes live in the main checkout's `.planning/` (it is gitignored, so the worktree has none): create `<main checkout>/.planning/YYYY-MM-DD-simplify-<slug>/` for the map, the findings and the plan.

### 1. Map the blast radius

Resolve the target to a seed set of files. For a symbol, Grep for its declaration. Then build the map in `map.md`:

- **Seed**: the files under review, with their line counts, their `public` and `internal` members, and which component (library or app) they belong to.
- **Callers**: for each seed type and member, Grep `src/`, `tests/` and `samples/` with word boundaries (`\bGuardrailRunner\b`, `\.RunAsync\(`, `nameof(GuardrailRunner`). Skip `bin/`, `obj/` and `.claude/worktrees/`. Confirm each hit refers to the seed and not a same-named type elsewhere by checking the namespace or `using`. This repo has many deliberate same-named types: `GitCli`, `ProcessRunner` and `LoggingProcessRunner` exist in both UpgradeAgent and RepoKit or ReadmeChecker, and each app has its own `AppServices`, `Program` and `Cli`, `Config`, `Run` folders. A bare substring match pulls in the wrong ones.
- **Wiring**: DI registrations in each app's `AppServices.cs` and `Program.cs`, options binding and validation under `Config/`, `appsettings.json` keys, `InternalsVisibleTo` in the csproj.
- **Callees**: types the seed depends on. Mark any that have the seed as their only consumer. Single-consumer abstractions are candidates in their own right, unless they are on the list above.
- **Tests**: the mirrored test project and folder plus every test file the caller Grep found. Note which entry points each pins, and whether an integration test replays a recording through the seed.
- **Decisions**: Grep `AGENTS.md`, the component's `README.md`, `PLAN.md`, `docs/` and `git log --oneline -- <seed files>` for the seed's names. A shape that one of these chose on purpose is refuted later with that citation, not re-litigated.

If the caller set exceeds about thirty files, list them all in the map but review in depth only those in the seed's own component and the tests. Say so in the map.

### 2. Review from five angles

Launch five agents in one message so they run in parallel, each with `model: opus`. Each gets the target, the path to `map.md`, the "What is not a simplification here" list, and one angle. Agents read the files themselves; do not paste code into the prompt. Each returns findings in the format below and nothing else.

**Reuse.** Code in the seed that re-implements something its own component already has: Grep the component's shared folders (`Infrastructure/`, `Internal.cs`) and the seed's neighbours for the helper, and name it. Also the reverse: near-duplicates of the seed's logic elsewhere in the same component that should call the seed instead. A helper in another component cannot be referenced; that duplication is deliberate and is not a finding.

**Simplification.** Redundant or derivable state, copy-paste with slight variation, deep nesting where guard clauses would flatten it, dead code (non-public members with no references outside the seed and its tests, parameters every caller passes the same value for, branches unreachable given the callers in the map), speculative generality (an interface with one implementation and one consumer that is not on the list above, a factory that always builds the same thing), and types past about 200 lines that mix distinct responsibilities. Name the simpler form.

**Efficiency.** Repeated process launches, git calls or file reads for the same value, independent operations run sequentially, LINQ chains materialised more than once, allocations on hot paths, blocking work added to startup. Name the cheaper alternative. Never propose running two agent sessions at once in different directories on one backend, or caching across repos or runs without naming where the cache is invalidated.

**Fit to callers.** Given the map, is the seed doing what its callers actually need, at the layer they need it? Look for parameters or options that exist for one caller, special cases layered on shared code that should be a change to the shared mechanism, work the seed does that every caller then undoes, and responsibility that sits one layer away from the data it needs. Name the general change, staying inside the component boundaries above.

**Conventions.** Read `AGENTS.md`, `.editorconfig`, and the `README.md` of the seed's component. Flag only what you can quote: the rule and the line that breaks it. The rules most often broken in a mature file are comments (none are allowed in `.cs` files outside `fixtures/`), magic strings, records for data carriers, `internal` unless it must be public, `sealed`, primary constructors, `_camelCase` private fields, braces always and file-scoped namespaces.

### 3. Consolidate, verify, plan

Merge the five reports into `findings.md`. Dedup by mechanism, keeping the version with the most concrete cost.

Verify before planning. Send the candidates in batches of up to five to a verifier agent launched with `model: opus`. The verifier reads the code and the map and returns one verdict per candidate:

- **CONFIRMED**: the simpler form is named, it preserves behaviour, and every caller that changes is enumerated.
- **REFUTED**: the complexity is load-bearing. Quote the caller, test, `AGENTS.md` rule, README section or commit that proves it.
- **DEFERRED**: real, but the fix changes observable behaviour, reaches well past the map, crosses a component boundary or needs a recording re-made. It becomes a follow-up note, not an edit.

Write `plan.md`: the map summary, confirmed findings in apply order (callee before caller, so each commit builds), refuted findings with the citation, deferred findings with the follow-up they would become.

Then review the plan. Launch a general-purpose agent with `model: opus`, give it the plan path and ask it to read `AGENTS.md`, `map.md` and every file the plan touches, then stress-test the plan: findings that break a rule in `AGENTS.md` or the list above, behaviour changes presented as refactors, callers the plan missed, apply orders that leave a commit that doesn't build, and tests that would need changing but aren't named. Ask for each issue rated critical, important or minor with the file and line it rests on. Verify each of its claims against the code, and fold in the ones that hold.

Then stop and show the user the plan path, the counts and the one-line summary of each confirmed finding. Ask whether to apply all, a subset, or none. `--apply` skips this pause.

### 4. Apply, one finding per commit

For each confirmed finding, in plan order:

1. Make the change. Every `.cs` file touched comes out of the edit with no comments left in it, whatever it had before. If the finding touches code duplicated between UpgradeAgent and RepoKit, decide whether the other copy needs the same change and say which you chose in the commit body.
2. `dotnet build UpgradeAgent.slnx`. Warnings are errors and `.editorconfig` style is enforced at build time.
3. Run the test projects the map names for this finding. If a test fails because it asserted the shape just removed, decide which it was pinning: behaviour, in which case the finding was wrong and gets reverted and moved to refuted, or implementation, in which case the test is updated and the plan notes it. Mutation-test any test you change: break the code, watch it fail, restore it.
4. Commit in the repo's style, `<Component>: <what got simpler>` (for example `ReadmeChecker: ...`), with the finding's cost line in the body.

One finding per commit so a bad one reverts alone. If a finding turns out to need the callers changed in a way the plan did not foresee, stop, update the plan, and take that back through the verifier before continuing.

### 5. Gate and report

When every finding is applied:

- `dotnet build UpgradeAgent.slnx`
- Every test project listed under Commands in `AGENTS.md`, including `tests/UpgradeAgent.IntegrationTests`, which replays the recordings end to end.
- If a `public` member or documented behaviour of a component changed, update that component's `README.md` in the same commit.
- Apply the "Before every commit" rule in `AGENTS.md`: if the run revealed a convention or gotcha another session must know, update `AGENTS.md`.

Report in three lists: applied (commit and one line each), refuted (with the citation), deferred (with the follow-up wording). Then ask whether to push and open a PR. After a successful push, remove the worktree as `AGENTS.md` describes.

## Finding format

Every agent and the verifier use this shape, one block per finding. The example is illustrative:

```
file: src/UpgradeAgent/Guardrails/Checks.cs
line: 120
angle: simplification
summary: three checks repeat the same load-diff, filter-by-extension, collect-violations shape
simpler_form: one private helper taking the extension set and the violation factory, called three times
cost: three copies of ten lines; a fix to the filter has already landed in only two of them
callers_changed: none outside the seed
risk: GuardrailRunnerTests pins all three checks through the runner
```

`cost` states what is duplicated, wasted or harder to maintain, in concrete terms. `risk` names the tests that would catch a mistake, or says there are none.
