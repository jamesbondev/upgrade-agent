# UpgradeAgent: a NuGet maintenance agent (.NET + Microsoft Agent Framework + Azure DevOps)

_Revision 3 (2026-09-23). Revision 2 folded in two independent reviews (Opus and Sonnet). Revision 3 switches the agent backend to **GitHub Copilot** (user decision), with a harness/`IChatClient` fixer kept as a later option. Every Copilot claim below was verified by running it locally._

## Goal

Build a **target-agnostic** console tool that keeps a .NET repo's NuGet dependencies up to date:

1. Detect outdated packages (deterministic).
2. Apply version bumps (deterministic).
3. Use an AI agent to fix whatever breaks until build and tests pass.
4. Verify the result with guardrails that run outside the agent (deterministic).
5. Open a draft pull request in Azure DevOps.

The live demo target is the `interest_accrual` repo at work. The tool is built and tested here against a small local fixture, then pointed at `interest_accrual` through **config only**. No code may assume anything about `interest_accrual`.

The demo must be reliable enough to run live, and resettable:

- Reliability comes from **record/replay** (section 9) and from demo controls such as `--only` and a cached baseline.
- Resetting comes from a reset script (section 11).

## Verified API notes

Checked 2026-09-23 against source in `microsoft/agent-framework` main and nuget.org. Re-check before coding. Do not guess names.

| Package | Version | Notes |
|---|---|---|
| `Microsoft.Agents.AI.Harness` | 1.22.0 (stable) | `HarnessAgent`, `AsHarnessAgent(HarnessAgentOptions)` |
| `Microsoft.Agents.AI.Tools.Shell` | 1.22.0-preview | `LocalShellExecutor`, `ShellPolicy`, `ShellEnvironmentProvider` |
| `Microsoft.TeamFoundationServer.Client` | 20.256.2 (20.279 preview) | `GitHttpClient`, `GitPullRequest.IsDraft`, `.Labels`, `CreatePullRequestLabelAsync` |
| `Microsoft.VisualStudio.Services.InteractiveClient` | 20.256.2 | `VssAzureIdentityCredential(TokenCredential)`: Entra auth for `VssConnection`, with refresh |
| `Spectre.Console` | latest stable | See the UI note below |
| `Microsoft.Agents.AI.GitHub.Copilot` | 1.22.0 | `GitHubCopilotAgent(CopilotClient, SessionConfig, ownsClient, name…)`; depends on `GitHub.Copilot.SDK` 1.0.11 |

**GitHub Copilot backend (M2, verified locally):**
- `CopilotClient` uses the local Copilot CLI login (`GetAuthStatusAsync` → `authType: user`). For pipelines, `CopilotClientOptions.GitHubToken`.
- **Copilot owns the tool loop.** It's an `AIAgent`, not an `IChatClient`, so it can't sit inside `AsHarnessAgent`, and there's no model-call seam for a `DelegatingChatClient`.
- **Built-in tools:** `bash` (`powershell` on Windows), `read_bash`, `stop_bash`, `list_bash`, `view`, `create`, `edit`, `grep`, `glob`, `web_fetch`, `skill`, `sql`, `task`, `read_agent`, `list_agents`, `write_agent`. We exclude `task` and the agent tools (sub-agents were spawned to route around refusals), `skill`, `sql`, and `web_fetch` (unless allowed).
- **Permissions:** `SessionConfig.OnPermissionRequest(PermissionRequest, PermissionInvocation) → PermissionDecision` (`GitHub.Copilot.Rpc`, marked `[Experimental("GHCP001")]`).
  - `PermissionRequestShell` gives `FullCommandText`, `HasWriteFileRedirection` and `PossiblePaths`. Its `Commands[].Identifier` is the whole command, not the program name, so we parse it ourselves.
  - `PermissionRequestWrite` gives `FileName` and `Diff`. `PermissionRequestRead` gives `Path`. `PermissionRequestUrl` gives `Url`.
- **Isolation switches used:** `WorkingDirectory`, `EnableConfigDiscovery`, `EnableFileHooks`, `EnableSkills`, `SkipCustomInstructions`, `EnableOnDemandInstructionDiscovery`, `EnableHostGitOperations`, `ExcludedTools`, `SystemMessage { Mode = Append }`.
- **Environment:** `CopilotClientOptions.Environment` **replaces** the runtime's environment (verified: a variable left out was `unset` in the agent's shell), so secrets are removed by omission.
- **Events** (`SessionConfig.OnEvent`):
  - `ToolExecutionStartEvent` (`ToolName`, `Arguments`, `ShellToolInfo.DisplayCommand`)
  - `ToolExecutionCompleteEvent` (`Success`, `Result.Content`, `Error.Message`)
  - `AssistantMessageEvent`
  - `AssistantUsageEvent` (`Model`, `InputTokens`, `OutputTokens`, `Cost`, `CopilotUsage.TotalNanoAiu`)
- **Models fall back silently.** On the personal account used here, every requested model (`claude-sonnet-4.5`, `gpt-5`, …) was actually served by `gpt-5-mini` or `claude-haiku-4.5`; `ListModelsAsync` returned only `auto`. The served model is read from `AssistantUsageEvent.Data.Model` and reported, with a warning when it differs from the one requested.

- **`HarnessAgentOptions`:**
  - Settings: `HarnessInstructions`, `ChatOptions`, `AIContextProviders`, `MaxContextWindowTokens`, `MaxOutputTokens`, `MaximumIterationsPerRequest`, `ToolApprovalAgentOptions`, `AgentModeProviderOptions`.
  - Switches: `DisableAgentModeProvider`, `DisableAgentSkillsProvider`, `DisableFileMemory`, `DisableWebSearch`, `DisableTodoProvider`, `DisableCompaction`, `DisableToolAutoApproval`.
  - `FileAccessStore` and `LoopEvaluators` are `[Experimental("MAAI001")]`.
  - The mode provider defaults to `"plan"`, so it must be disabled for unattended runs.
- **`LocalShellExecutorOptions`:**
  - Members: `Mode` (default Persistent), `WorkingDirectory`, `ConfineWorkingDirectory` (default true), `Timeout`, `Environment` (a null value removes a variable), `CleanEnvironment`, `Policy`, `Shell`, `ShellArgv`, `MaxOutputBytes`, `AcknowledgeUnsafe`.
  - `Timeout` defaults to **none**.
  - The local shell **is not a sandbox**. `ShellPolicy` is a regex pre-filter, not a security control.
- **Approval:**
  - `ToolApprovalAgentOptions.AutoApprovalRules` has type `IEnumerable<Func<ToolAutoApprovalRuleContext, ValueTask<bool>>>`.
  - `MaxAutoApprovalIterations` defaults to **40**. Each auto-approval re-invokes the inner agent; after 40, the request goes to the caller.
  - `FileAccessProvider.ReadOnlyToolsAutoApprovalRule` and `AllToolsAutoApprovalRule` exist.
  - Approval requests surface as `ToolApprovalRequestContent`. Answer with `request.CreateResponse(bool)` on the same session.
- **Structured output:** `AIAgent.RunAsync<T>` exists. It is non-streaming and sets `ResponseFormat` for the whole run.
- **Todos:** there is no change event. Poll `agent.GetService<TodoProvider>().GetAllTodosAsync(session)`, as the samples do.
- **Usage:** arrives as `UsageContent` in streaming updates.
- **`dotnet package list --outdated [--highest-minor|--highest-patch] --format json`:**
  - Shape: `projects[].frameworks[].topLevelPackages[] { id, requestedVersion, resolvedVersion, latestVersion }`.
  - A package with no newer version in its current major is **absent** from the `--highest-minor` output.
  - When a feed can't be reached, the command prints **plain text (not JSON) and exits 1**.
- **Spectre.Console:** live display "is not thread safe. Using it together with other interactive components such as prompts… is not supported." (verified in the docs).
- **TRX test names:** theory rows embed their arguments, e.g. `T.UnitTest1.R(id: Id { Value = abc })` (verified locally). A parameter type change therefore renames tests.
- **ADO client and warnings-as-errors:** `Microsoft.TeamFoundationServer.Client` 20.256.2 with `TreatWarningsAsErrors` fails restore on SDK 10.0.112 with NU1605 (the `System.Configuration.ConfigurationManager` downgrade) and NU1902 (`System.Security.Cryptography.Xml` 5.0.0). Reproduced locally.

## Tech constraints

- .NET 10, C#, nullable enabled, `TreatWarningsAsErrors` on in UpgradeAgent itself.
  - Pin `System.Configuration.ConfigurationManager` and `System.Security.Cryptography.Xml` directly in UpgradeAgent's own CPM file.
  - `<NoWarn>` MAAI001 **only** in the project that uses the experimental harness features.
- Chat provider is configurable via `IChatClient`. Default is Azure OpenAI / Foundry, and it must stay swappable.
- The Copilot runtime picks the shell (bash on Linux/WSL, PowerShell on Windows). The approval parser understands both families; PowerShell 7 is still needed for the scripts.
- Console UI: Spectre.Console, **append-only rendering** (see section 5).
- Local source control: git CLI.
- Pull requests: Azure DevOps client libraries. No GitHub, Renovate or Dependabot.

## Solution layout

```
UpgradeAgent/
  src/UpgradeAgent/
    Detection/     # package list parsing, classification, policy, grouping, TFM compatibility
    Bumping/       # deterministic XML version edits
    Agent/         # harness setup, instructions, approval rules (parsers), run meter
    Guardrails/    # build/test/diff checks, TRX parsing, baseline cache
    Publishing/    # commits, PR description, ADO client, push_branch tool, git auth
    Replay/        # record/replay DelegatingChatClient
    Ui/            # append-only Spectre renderer + approval prompter
  tests/UpgradeAgent.Tests/
  fixtures/SampleRepo/                     # LoanLedger source; .feed/ holds the committed Fixture.Lib nupkgs
  fixtures/Fixture.Lib/                    # one source tree, packed as 1.0.0 / 1.1.0 / 2.0.0 via -p:FixtureApi
  fixtures/appsettings.fixture.json        # config pointing UpgradeAgent at the generated fixture
  scripts/build-fixture.ps1                # generates artifacts/fixture/SampleRepo (gitignored)
  scripts/reset-demo.ps1
  recordings/                              # gitignored except recordings/fixture*.jsonl
  appsettings.json
  README.md                                # includes the optional pipeline section
  azure-pipelines.yml                      # optional, last milestone
```

## 1. Target repo support (generic)

The target is set by `Target.RepoPath` and `Target.Solution` (`.sln` or `.slnx`).

**Supported:**

- Central Package Management: `Directory.Packages.props`.
- Per-project `<PackageReference Version="...">`.
- If `packages.lock.json` is present, restore with `--force-evaluate`. Lock file diffs are expected.

**Reported as "manual", not bumped:**

- versions set through MSBuild properties
- `VersionOverride`
- floating or range versions

**Unsupported:** `packages.config` and non-SDK-style projects. Detect them, fail fast and explain why.

**Startup preflight** (printed as a checklist; any failure aborts):

- the SDK
- the solution exists
- the target repo is clean
- restore succeeds (including private feed auth)
- baseline build and tests pass (section 6)
- no config files leak into the worktree (section 5)
- `recordings/` is gitignored for non-fixture targets

## 2. Development fixture (not the demo)

- **`SampleRepo`:** a class library and an xUnit test project (~8 tests), using CPM.
  - It references one public package a patch behind.
  - It references `Fixture.Lib` 1.0.0 from a local folder feed. `packageSourceMapping` sends `Fixture.*` to the local feed and `*` to nuget.org.
  - The feed lives **inside** the repo (`.feed/`, committed), so worktrees and pipelines see it with no path tricks.
  - `build-fixture.ps1` deletes `fixture.lib` from the global packages folder on every run, so repacking never serves stale cache entries. (A repo-local `globalPackagesFolder` was dropped: it would give every worktree a cold cache.)
  - The generated repo's first commit is **deterministic** (fixed identity, dates, committed nupkgs, `.gitattributes`), so a recording made on one machine replays on another. `-Repack` rebuilds the nupkgs and deliberately invalidates recordings.
  - At least one `[Theory]` passes a value whose type changes in v2. This deliberately exercises test-name normalisation.
- **`Fixture.Lib` v2.0.0** has three breaking changes:
  - a method rename
  - a sync method replaced by an async one taking a `CancellationToken`
  - a `string` parameter replaced by a strongly typed ID record
  - `MIGRATION.md` and `CHANGELOG.md` are packed into the nupkg.
- **`Fixture.Lib` v1.1.0** marks the old APIs `[Obsolete("Use X instead")]`.
- `build-fixture.ps1` copies `fixtures/SampleRepo` to `artifacts/fixture/SampleRepo`, runs `git init` plus the deterministic initial commit, and verifies the baseline (16 tests).

## 3. Detection (deterministic)

1. Run `dotnet package list --outdated --format json`, plus `--highest-minor`, plus `--highest-patch` (for `0.x` packages).
   - A non-zero exit or non-JSON output is a hard error that shows the raw output (usually feed auth).
   - Set `NuGetAudit=false` for detection runs only.
2. Merge by **(project, framework, package id)**. Projects can be on different versions of the same package.
3. **Two-step majors:**
   - If a package has a newer major and also appears in the minor-step output, it gets two steps: step 1 goes to the latest minor, step 2 from that minor to the latest major.
   - If it is absent from the minor-step output, it gets only the major step.
   - For `0.x` packages, the minor step comes from `--highest-patch`, because `0.x` minors count as major.
4. Classify each update by SemVer: patch, minor or major. A `0.x` minor counts as major.
5. **Policy:**
   - `Allow` / `Deny` lists by ID or glob. Deny entries can carry a reason, e.g. `FluentAssertions: "commercial licence from v8"`.
   - `MaxAutoBump`.
   - `AttemptMajors`.
   - **`MaxMajorJump`** (M7): major steps further than this are `Manual`.
   - **`TargetOverrides`:** pin exact target versions per step.
   - **`Groups`:** named glob sets that must move together. Defaults: `Microsoft.EntityFrameworkCore*`, `Microsoft.Extensions.*`, `Microsoft.AspNetCore.*`, `xunit*`, `OpenTelemetry*`, `Serilog*`, `Polly*`.
6. **TFM compatibility:**
   - For each major target, read the nuspec dependency groups and lib folders, from the package in the global packages folder or from the flat-container API.
   - If the target needs a newer TFM than a project uses, mark the update **"needs TFM upgrade"** and skip it. Changing the TFM is forbidden to the agent.
7. **Grouping:**
   - One patch/minor group.
   - One group per major family (a `Groups` entry) or per standalone major package.
   - Groups run in that order, each on top of the previous accepted commits.
   - If an earlier group is rejected, recompute later groups' "from" versions from the files, not from the plan.
8. **Demo controls:**
   - `--only <id|group>` limits the run.
   - `--plan <file>` loads a frozen plan instead of detecting. Replay does this automatically.
9. Render the plan table: package, project(s), from, to, bump, group, and the policy/compatibility decision with its reason.

## 4. Bumping (deterministic, not the agent)

For each group, the **app** edits version entries surgically, preserving whitespace and comments, then runs `dotnet restore` in the worktree.

- **The app does all restores.** The agent can only build and test with `--no-restore`.
- The agent never edits version entries.

## 5. The agent (GitHub Copilot)

The agent plugs in through `IGroupFixer`. It's only called when a group's build or tests fail after the bump. `CopilotFixer` is the M2 implementation; `NoAgentFixer` (`--agent none`) rejects such groups. A harness/`IChatClient` fixer can be added later behind the same interface.

### Worktree

- The worktree lives **next to the target repo**: `<targetParent>/.ua-work/<runId>` (configurable with `Target:WorkRoot`).
- It must never sit under the UpgradeAgent folder. MSBuild and NuGet read `Directory.Build.props`, `global.json` and `nuget.config` from parent folders.
- **Check at run start:** compare the config files found between the worktree and the drive root with those found above the original repo. Any difference aborts.

### Session (one per group)

- **Client:** one `CopilotClient` per run, with `WorkingDirectory` set to the worktree.
  - Its `Environment` is the app's environment minus secrets, plus `MSBUILDDISABLENODEREUSE=1` and `DOTNET_CLI_USE_MSBUILD_SERVER=0`.
  - Secrets are removed by name (`SYSTEM_ACCESSTOKEN`, `ADO_PAT`, `GH_TOKEN`/`GITHUB_TOKEN`, `VSS_NUGET_*`, …), by prefix (`AZURE_`, `ARM_`, `AWS_`), and by fragment (`_PAT`, `SECRET`, `PASSWORD`, `API_KEY`, `CONNECTIONSTRING`, `ACCESS_TOKEN`).
- **Session settings:**
  - Repo-driven behaviour is off: `EnableConfigDiscovery`, `EnableFileHooks`, `EnableSkills`, on-demand instruction discovery and host git operations are all false, and `SkipCustomInstructions` is true.
  - The excluded tools are listed in the API notes.
  - The instructions are appended with `SystemMessage.Mode = Append`.
- **Unchanged from M1:** Azure DevOps tokens are only acquired at publish time.

### Approvals (`CommandPolicy`, a parser, applied in `OnPermissionRequest`)

**Shell commands.** A quote-aware parser splits the command on `&&`, `||`, `;` and `|`, then evaluates each segment.
- **Refused outright:**
  - `$(…)`, backticks, redirection (except `2>&1`), `&`, script blocks and multi-line commands
  - paths that resolve outside the worktree or the NuGet global packages folder (`cd` is tracked)
  - `dotnet restore/add/remove/package/nuget/list`
  - MSBuild `-p:` overrides
  - `dotnet build` without `--no-restore`, and `dotnet test` without `--no-build` or `--no-restore`
  - git anything except `status/diff/log/show/ls-files/grep/blame`
  - `find -exec/-delete`
  - `sed -i`
- **Auto-approved:** `dotnet build`/`test` with allow-listed flags, `cd` inside the worktree, and read-only commands (bash and PowerShell). Pipes are allowed when every segment is allowed.
- **Operator:** anything else, e.g. `rm`, `curl`, scripts or `dotnet format`. **Changes in M7:** these are refused with feedback instead; see "Autonomy and scope limits".

**File access.**
- **Writes:** auto-approved for source files (`.cs/.fs/.vb/.razor/.cshtml`) inside the worktree. Other files go to the operator. `.git/` and paths outside the worktree are refused.
- **Reads:** allowed inside the worktree and the global packages folder; refused elsewhere.
- **URLs:** refused unless `Agent:AllowWebFetch`, which then goes to the operator.
- **Anything else** (MCP, memory, custom tools…): refused.

**Every refusal carries actionable feedback**, e.g. "Add --no-restore: packages are already restored."

**Non-interactive** (`--non-interactive` or redirected output): operator requests are declined, never left waiting.

### Migration notes: inlined, and read before edits

Observed in the first live run: `gpt-5-mini` ignored the listed `MIGRATION.md` and "fixed" the build by re-implementing library code. The tests stayed green, so the guardrails couldn't catch it. So:

- The app finds docs in the package folder: `MIGRATION*`, `CHANGELOG*`, `BREAKING*`, `UPGRADING*`, `RELEASE*NOTES*`, `README*` and `docs/*.md`, plus the nuspec `releaseNotes`, `projectUrl` and `repository` fields.
- **Migration docs up to 16 KB in total are pasted into the task.**
- Larger ones are listed as "READ FIRST". **Edits are refused until each has been read**; a view, `cat` or `grep` mentioning the path counts.
- The instructions forbid re-implementing library code, or changing constructors and dependencies to dodge a new API.
- The current build errors (up to 25, with repo-relative paths) are in the task, which saves the agent a first build.

With those changes, the same weak model did a textbook migration: `FormatValue`, `GetConfigAsync` with the `CancellationToken` flowing through, `Async` renames, and `new AccountId(...)`.

### Budgets

- Default caps per group: wall time 10 min (linked `CancellationTokenSource`) and 80 tool calls (counted from `ToolExecutionStartEvent`).
- M7 adds a refusal cap, a no-progress stop and an error-count check before the agent starts (see "Autonomy and scope limits").
- Exceeding either cancels the run. The orchestrator then rebuilds, retests, and rejects and reverts as usual.
- **Stats per group:** served model, model calls, tool calls, input/output tokens, Copilot AI credits (`TotalNanoAiu`), operator approvals and refusals.
- **Ctrl+C** stops the current group and reverts it (this path is still untested).

### Autonomy and scope limits (M7)

Observed in the first run on a work repo: the agent asked the operator for `curl` and other commands it couldn't find a use for, most likely because it couldn't find the replacement API. One package was 8 majors behind (8 → 16) and was handed to the agent as a single major step.

**Decisions:**

1. **Policy: reject by default, ask only for build files.**
   - Unknown shell commands, other `dotnet` verbs (`format`, `run`, …) and `rm`/`mv`/`cp` move from operator to **refused**.
   - Each refusal says what to do instead. Network tools (`curl`, `wget`, `Invoke-WebRequest`, `iwr`, `irm`) get: "No network access. Migration notes are in the task and the NuGet packages folder. If the replacement API isn't documented there, report the error as unresolved."
   - Edits to non-source files (`.csproj`, `Directory.Build.props`, …) stay with the operator. `--non-interactive` still declines them.
   - `AllowWebFetch` is unchanged.
2. **No-progress stop (per group, in `CopilotFixer`).** Two checks, either one cancels the session with its own `BudgetReason`:
   - **Refusals:** `Agent:MaxRefusalsPerGroup`, default 5.
   - **Build errors:** on each completed `dotnet build` tool call, parse the error count from the result with `BuildOutputParser`. If `Agent:MaxBuildsWithoutProgress` (default 3) builds in a row don't go below the lowest error count so far, stop. The build errors in the task count as the starting point. A successful build resets the counter.
   - The stop goes down the existing budget path: rebuild, retest, guardrails, reject and revert.
   - The run output and PR show which check stopped the agent.
3. **Large version jumps become `Manual`.**
   - `Policy:MaxMajorJump`, default 2 (`to.Major - from.Major`). A major step over the limit is `UpdateDecision.Manual` with the reason "N major versions behind (limit M); upgrade manually or set a TargetOverride".
   - The minor step to the latest release in the current major still runs, because it is non-breaking.
   - `TargetOverrides` to a closer major is the way to move forward in stages; an override is still checked against the limit.
   - `0.x` packages are exempt, since their minors already count as majors.
4. **Error-count check before the agent starts (`RunOrchestrator`).**
   - After bump, restore and build, if the build has more than `Agent:MaxErrorsForAgent` errors (default 50), the agent isn't called. The group is rejected with "N build errors after the bump (limit M); too large for the agent" and reverted.
   - Test failures don't count; only build errors are compared.

**Prompt:** one line added to `FixInstructions.System`: "If the migration notes and the code don't show a replacement, report the error as unresolved. Don't search the web, download packages or look outside the repository."

**Config:** `Agent:MaxRefusalsPerGroup`, `Agent:MaxBuildsWithoutProgress`, `Agent:MaxErrorsForAgent` and `Policy:MaxMajorJump`. Setting any to 0 disables that check.

**Tests:**
- `CommandPolicyTests`: `curl`, `rm`, `dotnet format` and unknown commands are refused with feedback; `.csproj` edits still ask. Existing Ask expectations are updated.
- Planner: 8 → 16 is `Manual` with the minor step still planned; 8 → 10 is planned; an override to 10 is planned; an override to 16 is `Manual`; `0.x` is exempt; `MaxMajorJump: 0` disables the check.
- No-progress meter as a pure class: error counts that drop keep going; 3 flat builds stop; a green build resets; refusals over the limit stop.
- Orchestrator: a group over `MaxErrorsForAgent` is rejected without calling the fixer (fake `IGroupFixer` asserts it wasn't called).
- Both replay integration tests still pass unchanged. Replay doesn't go through `CommandPolicy` or the meter, so the recordings stay valid.

**Done when:** unit and integration tests pass; a live fixture run still fixes Fixture.Lib 2.0 with no operator prompts; a re-run on the work repo produces no operator prompts other than build-file edits, and shows the 8 → 16 package as manual in the plan.

### Instructions

See `Agent/FixInstructions.cs`; the wording is unit-tested. Summary:
- Run from the repo root, with the exact build and test commands (VSTest or MTP).
- The forbidden list, the same as the guardrails.
- Async: make callers async, flow `CancellationToken`, never block on tasks.
- Use the replacement APIs.
- Stop after three failed attempts at the same error.
- Build and test before finishing.
- In the patch/minor group, fix errors only and report deprecation warnings.

### Structured summary

- After the fix turn, a second turn on the same session asks for JSON only. Every permission is refused during this turn.
- The parser accepts bare or fenced JSON and returns null rather than throwing.
- Shown under the group, and later in the PR. Cross-checking it against the diff is M5.

### UI: append-only, no Live display

- **Why:** Spectre's live display can't be combined with prompts. Agent events arrive on background threads, so all agent output and prompts share one console lock.
- **What is shown:**
  - short agent narration lines
  - shell commands (`$`), with `→ build: 7 error(s) CS0117×3…` or `→ tests: 16 passed` parsed from their output
  - edits (`✎`)
  - quiet reads
  - refusals (`⊘`)
  - operator prompts, in a yellow panel
- **Audit log:** a full log per group in `out/run-*/agent/<group>.log`, including the task prompt.

## 6. Guardrails (deterministic, outside the agent)

### Baseline

The baseline is build and test on the clean worktree, recording:

- **per-method passed counts** (TRX `TestMethod className + name`, per TFM)
- the list of test files

It is cached per `(target commit, SDK version)` under `.ua-work/baseline/`, so rehearsals and replays skip it. If the baseline is red, the run aborts.

### Test runner detection

There are three modes:

| Mode | How to get TRX |
|---|---|
| VSTest | `--logger trx` |
| MTP within VSTest mode | `-- --report-trx` (needs `Microsoft.Testing.Extensions.TrxReport`) |
| MTP mode via `global.json` | `--report-trx` |

- `Target.TestArgs` passes raw extra arguments through, because filter syntax differs between frameworks.
- If TRX isn't available, fall back to a count-only check. It is labelled "weaker" in the output and in the PR.

### Per-group checks

1. **Git state:** HEAD still equals the group-start commit; the agent didn't commit or reset.
2. **Build:** `--no-restore` succeeds.
3. **Tests:** they pass, and every baseline test method still has at least as many passed rows. Methods are compared by class and method name, **not** the display name, because theory rows embed their arguments.
4. **Added lines** in `git diff -U0`, counting only lines whose content is **genuinely new** to that file (not re-added lines) and ignoring moved or reformatted lines, contain none of:
   - `#pragma warning disable`, `#nullable disable`, `#if false`
   - `NoWarn`, `WarningsNotAsErrors`, `<TreatWarningsAsErrors>false`
   - `SuppressMessage`
   - `Skip\s*=`, `[Ignore`, `[Explicit`, `Assert.Skip`
   - `<Compile Remove`
   - `dotnet_diagnostic.*severity`
   - a new `GlobalSuppressions.cs` file
5. **Package versions** changed only for this group's packages, to exactly their targets. There are no new `PackageVersion` or `VersionOverride` entries and no `TargetFramework`, `LangVersion` or `global.json` changes.
6. **Files:**
   - No test files were deleted.
   - `git status --ignored` shows no new ignored files outside `bin/` and `obj/`. Example: `*.csproj.user` is ignored by default yet imported by MSBuild.
7. **Warnings only** (never a rejection):
   - the assertion count in touched test files dropped
   - a test file was modified at all

   Both are listed in the PR description for the reviewer.

### Outcomes

**On failure:**

- Run `git reset --hard <group-start>` and `git clean -fd`.
- Mark the group rejected, with the reason.
- Continue with the next group.

**On success:**

- Stage tracked changes plus new `*.cs` files. Never `git add -A`.
- Commit with a message like `chore(deps): bump Foo 1.2.3 -> 2.0.0`, with the summary in the body.
- Append the commit SHA to the **guardrail ledger**.

## 7. Publishing

- **Branch:** `agent/nuget-updates-{yyyyMMdd-HHmm}` (UTC).
- **PR description:**
  - summary table
  - per-package breaking changes and fixes
  - upcoming deprecations
  - unresolved, rejected and "needs TFM upgrade" items
  - reviewer attention list: touched tests, assertion-count warnings, summary/diff mismatches
  - run stats: duration, turns, tokens, estimated cost

**The push goes through an approval-required agent tool.** After all groups, the app starts a **publish session** whose only tool is `push_branch`, wrapped in `ApprovalRequiredAIFunction`.

- The console shows the branch, remote and commit list. The operator answers yes or no; the app never issues a standing "always approve".
- When it runs, the tool **refuses** unless HEAD equals the last SHA in the ledger and the worktree is clean.
- If the model never calls the tool within 3 turns, the app reports "not pushed" and exits cleanly.
- In `--dry-run`, the prompt still appears. On approval, the tool prints the push it would run and does nothing.

**Git auth for the push:**

- Supply the auth header through `GIT_CONFIG_COUNT` / `GIT_CONFIG_KEY_0` / `GIT_CONFIG_VALUE_0` = `http.extraheader`. Never use `-c`, which shows the token in the process list.
- Entra tokens use `bearer <token>`; PATs use `basic base64(":" + pat)`.
- Set `GIT_TERMINAL_PROMPT=0` and pass `-c credential.helper=` so Git Credential Manager can't pop up on stage.

**Modes:**

- **`--dry-run` (the default):**
  - Writes `out/pr-description.md`.
  - Prints `git log`.
  - Nothing leaves the machine except calls to the model endpoint.
- **`--ado`:**
  1. Push.
  2. `CreatePullRequestAsync` with `IsDraft = true` and `Labels = [agent-generated]`, targeting the repo's `DefaultBranch`.
  3. Print the PR URL.
  - Auth is a PAT from an env var (Code: Read & Write) or `VssAzureIdentityCredential` with `AzureCliCredential` / `DefaultAzureCredential`.

## 8. Config (`appsettings.json` shape)

```jsonc
{
  "Target": { "RepoPath": "", "Solution": "", "TestArgs": [] },
  "Agent": {
    "Provider": "copilot",            // copilot | none
    "Model": null,                    // null = Copilot chooses; the served model is reported either way
    "ReasoningEffort": null,
    "MaxMinutesPerGroup": 10, "MaxToolCallsPerGroup": 80,
    "MaxRefusalsPerGroup": 5, "MaxBuildsWithoutProgress": 3, "MaxErrorsForAgent": 50,  // M7; 0 disables
    "AllowWebFetch": false,
    "RemoveEnvironmentVariables": [], // on top of the built-in secret patterns
    "GitHubTokenEnvVar": null         // pipelines: env var holding a token with Copilot access
  },
  "Policy": {
    "Allow": [], "Deny": [ { "Id": "FluentAssertions", "Reason": "commercial licence from v8" } ],
    "MaxAutoBump": "Major", "AttemptMajors": true, "MaxMajorJump": 2, "IncludePrerelease": false,
    "Groups": { "efcore": ["Microsoft.EntityFrameworkCore*"], "xunit": ["xunit*"] },
    "TargetOverrides": { /* "Some.Package": "2.1.0" */ }
  },
  "AzureDevOps": { "OrganizationUrl": "", "Project": "", "Repository": "", "PatEnvVar": "ADO_PAT", "UseAzureIdentity": false },
  "Pricing": { "InputPerMTok": null, "OutputPerMTok": null }
}
```

## 9. Record / replay (demo safety net)

With Copilot there's no model-call seam, because the model calls happen inside the Copilot runtime. So replay works at the **group** level, not the model-call level:

- **Record** (`--record <name>`) to `recordings/<name>/`:
  - `header.json`: the frozen plan, target commit, OS, SDK, requested and served model
  - per group: the agent's event log (the same lines as the console), the structured summary, and the **patch** of the agent's changes (`git diff` after the fix, before the app's re-check)
- **Replay** (`--replay <name>`):
  - Uses the frozen plan and applies the same deterministic bumps.
  - Instead of calling Copilot, it plays back the recorded agent lines at a readable pace and applies the recorded patch.
  - The app's **rebuild, retest and guardrails then run for real**, so the verdicts on screen are genuine.
  - Output is visibly labelled "replay".
- **Header check:** a different commit or SDK is a hard error. A patch that doesn't apply is a hard error, with `--replay-fallback live` available.
- **Honest limit:** replay doesn't re-execute the agent's intermediate tool calls. What it proves live is the bump → verify → commit pipeline around a recorded fix.

Recordings of work repos contain proprietary code. They are gitignored, and preflight checks that.

## 10. Tests

**Unit tests** cover:

- package list JSON: multi-project, multi-TFM, different versions per project, absent from minor output, `0.x`, and the non-JSON failure output
- the merge key
- policy, `Groups` and TFM compatibility
- XML bumping, preserving whitespace
- TRX parsing, including theory normalisation, and all three runner modes
- every diff rule, including moved or reformatted lines that must **not** trigger
- the approval-rule parser, including metacharacter and path-escape cases
- the git auth env builder
- the PR renderer
- replay divergence

**Integration tests** (replay mode, no model):

- `fixture` recording: all guardrails pass, Fixture.Lib ends at 2.0.0, test methods are preserved, `out/pr-description.md` exists.
- **`fixture-cheat` recording:** a hand-crafted "bad agent" that adds `#pragma warning disable` and a `Skip =`. The guardrails must **reject** it and revert.

## 11. Reset

`scripts/reset-demo.ps1 -RepoPath <path> [-Ado]`:

1. `dotnet build-server shutdown`, which releases file locks on Windows.
2. `git worktree remove` for `.ua-work/*` entries, then `git worktree prune`.
3. Delete local `agent/nuget-updates-*` branches and clear `out/`. The baseline cache is kept.

With `-Ado`:

- List active PRs with the `agent-generated` label and matching source branches, using `az repos`.
- Show them and ask for confirmation.
- Abandon them and delete the remote branches.

The target's default branch is never touched.

## 12. Optional pipeline (last milestone)

- A weekly `azure-pipelines.yml` using `--ado --non-interactive`.
- Check out both repos, with `persistCredentials: false`.
- `NuGetAuthenticate@1` handles private feeds. Copilot authenticates with a GitHub token that has Copilot access, from a secret variable referenced by `Agent:GitHubTokenEnvVar`. Check the org's Copilot policy allows CLI/SDK use.
- `System.AccessToken` is passed to the publish step only.
- In non-interactive mode, `push_branch` is auto-approved only when `TF_BUILD=True`. **Document that this is a guard against running it by accident, not a security control.** The human gate is the draft PR review.
- Required permissions on the target repo: Contribute, Contribute to pull requests, Create branch.

## 13. README

Include:

- **Prerequisites:** .NET 10 SDK, PowerShell 7, git, and `az` (for `reset -Ado`).
- Fixture setup and configuration.
- **Work-repo checklist:**
  - SDK-style projects
  - CPM or per-project versions
  - test runner mode
  - **the build+test time budget** (aim for under 60 s, or use `TestArgs` filters)
  - private feed auth
  - a package at least one major behind that breaks at the call sites
  - package families
  - TFM headroom
  - approval to send code to the model endpoint
- **Rehearse and record:**
  1. `--only <package>` and `--record demo`
  2. review the result
  3. reset, keeping the baseline cache warm
- **5-minute demo script:**
  1. detection table
  2. `--replay demo --only <package> --dry-run`
  3. narrate the fixes
  4. guardrails
  5. push approval
  6. PR description
  7. optionally `--ado`
  - Include a fallback line: "Ctrl+C reverts and still produces a partial report."
- **Honest security note:** the local shell runs as the operator. The approval rules and guardrails reduce risk but aren't a sandbox. Containers are the production hardening step.
- **How this would work in production:**
  - a per-repo pipeline
  - least privilege (only the publish step holds a write token)
  - human review of every PR
  - cost per run from `RunMeter`

## Milestones (each ends demo-able)

Status (2026-09-23):
- **M0 done.** `UpgradeAgent plan`, checked against real nuget.org data (EF Core 10 on net8.0 is marked "needs TFM upgrade").
- **M1 done.** `run` without an agent, plus `reset-demo.ps1` (local part). Includes the guardrail "bad agent" tests.
- **M2 done, using GitHub Copilot** (switched from the harness by user decision). `run --agent copilot` (the default) fixes the Fixture.Lib 2.0.0 break live.
  - Latest fixture run: about 2 min for the major group, 22 model calls, 21 tool calls, about 373k input tokens. All guardrails passed, and the two modified test files were flagged.
  - 180 unit tests. Mutation-checked: disabling the path-escape or `-p:` checks fails 4 tests.
- **M3 done.**
  - **Ctrl+C:** a real SIGINT during an agent edit reverted the group and still wrote the summary and `run.json`. The grace period was raised from 2 s to 30 s.
  - **Operator prompt:** tested with Spectre's `TestConsole`.
  - **Reviewer notes:** removed public signatures in non-test code, and disagreements between the agent's summary and the diff.
  - **TRX evidence:** kept per group.
- **M4 done** (group-level replay, section 9).
  - `--record <name>` / `--replay <name>` / `--replay-max-gap`. Replaying the fixture takes 36 s, against 110 s live.
  - `recordings/fixture` is a real Copilot session; replay accepts it.
  - `recordings/fixture-cheat` is hand-crafted from the honest patch plus a `#pragma` and a skipped test. The build is green (15 passed, 1 skipped), yet it's **rejected**: the per-method test check and the suppression scan both catch it.
  - `tests/UpgradeAgent.IntegrationTests` replays both from a fresh temp fixture with no model access, in about 30 s.
  - Fixes found along the way:
    - The fixture commit wasn't deterministic across filesystems (exec bit on ext4 vs drvfs); `core.fileMode=false` fixed it.
    - Recorded agent lines are now worktree-relative.
    - An SDK mismatch on replay is a warning; a commit mismatch is still an error.
- **M5 done** (publishing, dry-run).
  - `out/pr-description.md` (plus a copy per run) contains:
    - a summary table
    - per-group breaking changes, fixes and build warnings (each distinct message)
    - reviewer attention notes and collapsible guardrail details
    - agent stats, marked when replayed
    - a "Not included" list with reasons
    - run totals
  - The PR only states what the app verified; a rejected group's agent claims never appear as applied fixes (tested).
  - **`push_branch` with a live agent:**
    - Copilot gets a publish session whose only tool (`AvailableTools`) is `push_branch`, wrapped in `ApprovalRequiredAIFunction`.
    - The agent calls it, the framework raises an approval request, and the operator panel appears.
    - Verified in a real pseudo-terminal: the agent called it, "y" was entered, and the tool checked the ledger and printed its dry-run result.
  - **Without a live model** (replay, `--agent none`), the app calls `push_branch` directly behind the same prompt.
  - **The tool itself refuses** when there is nothing accepted, when HEAD isn't the last ledger commit, or when the worktree is dirty. It runs at most once.
  - A spinner now covers detection, so the terminal doesn't look hung (the user saw arrow keys echoed during the silent pause).
  - 201 unit tests and 2 integration tests.
- **M6 done** (Azure DevOps, verified against a real org: `jamesbond312/argus/LoanLedger-Demo`).
  - `run --ado` checks the credential and repo **before** the run, then publishes only through the approved `push_branch`, then creates a draft PR with the `agent-generated` label.
    - Verified two ways: with a replay (the app calls the tool), and live (Copilot calls the tool). Both created PRs (!2887, !2888), and both were then cleaned up.
  - **Credential sources:** `$ADO_PAT`, `$SYSTEM_ACCESSTOKEN` (bearer), user secrets `AzureDevOps:Pat`, or `DefaultAzureCredential` (az login).
    - Git gets the credential only through `GIT_CONFIG_COUNT/KEY/VALUE` (a host-scoped `http.extraheader`, with `credential.helper` blanked).
    - Verified: no auth config in the repo, and nothing written to `~/.git-credentials` even though the user's global helper is `store`.
    - The PAT and token env vars are always stripped from the agent's environment.
  - PR descriptions over 4,000 characters drop the `<details>` blocks, then truncate with the full text posted as the first comment.
  - `ado-seed` refuses a non-empty repo (verified). `ado-cleanup` (and `reset-demo.ps1 -Ado -Config`) lists first, only acts with a terminal confirmation or `--yes`, and never touches the default branch.
  - The `Microsoft.TeamFoundationServer.Client` NU1605/NU1902 errors are fixed by pinning `System.Configuration.ConfigurationManager` and `System.Security.Cryptography.Xml` to 10.0.12.

- **M0: detection.** Parsing, classification, policy, TFM check and plan table against the fixture.
- **M1: bumping and guardrails, no AI.** Worktree, baseline cache, bump, restore, guardrails, and a commit for the patch group. Also the `fixture-cheat`-style guardrail tests, using scripted diffs.
- **M2: agent (done, Copilot).** Includes what was planned as M3: full instructions, migration notes inlined and gated, budgets, structured summary, and Fixture.Lib 1.1→2.0 fixed live.
- **M3: hardening from live runs (done).** Ctrl+C, operator prompt, public-API and summary/diff reviewer notes.
- **M4: record/replay (done).** Group-level (see section 9). The `fixture` and `fixture-cheat` recordings are committed; the integration tests are green with no model.
- **M5: publishing in dry-run (done).** `push_branch` approval, ledger check and PR description. **This is the full rehearsal target.**
- **M6: Azure DevOps (done).** Draft PR, labels, PAT/token/Entra auth, seed and cleanup.
- **M7: autonomy and scope limits.** Reject-by-default policy, refusal and no-progress stops, `MaxMajorJump`, error-count check before the agent. From the first work-repo run (section 5, "Autonomy and scope limits").
- **M8: `interest_accrual`.** Config only. Work through the checklist, record the demo, rehearse.
- **M9: finishing.** Reset script, README, optional pipeline.

## Acceptance criteria

1. `build-fixture.ps1` succeeds on a fresh clone, and the fixture baseline is green.
2. Unit tests pass. Both replay integration tests pass with no model access: `fixture` is accepted and `fixture-cheat` is rejected and reverted.
3. A live `UpgradeAgent --dry-run` against the fixture does all of the following:
   - bumps the patch package
   - takes Fixture.Lib 1.0.0 → 1.1.0 → 2.0.0
   - fixes all breaking changes, including the renamed theory rows, and passes all guardrails
   - shows the push approval
   - writes `out/pr-description.md`
4. `reset-demo.ps1` restores the pre-run state, and a second run works, including on Windows.
5. The same binary runs against `interest_accrual` with config changes only.

## Explicitly cut

- The `dotnet list package` fallback.
- Bumping property-based versions (reported as "manual").
- `LoopEvaluator`, replaced by our own budgets.
- The Docker executor.
- Separate approval rule sets per OS (pwsh is pinned).
- A Live/refreshing UI.
