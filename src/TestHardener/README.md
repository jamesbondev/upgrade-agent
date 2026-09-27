# TestHardener

Runs [Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/) on the repos you list and finds the
mutants their tests don't catch. Stryker makes small changes to the code (a mutant), such as `>` to `>=` or removing
a statement, and runs the tests. A mutant the tests don't notice (a survivor) marks behavior no test checks.

`survey` reports the survivors worth a test, grouped by method and ranked. It changes nothing. `harden` has an agent
write tests that kill the top-ranked survivors, checks them with plain code and with Stryker itself, and opens a
draft pull request after you approve it (`--dry-run` saves a patch instead). It is built on [AgentHarness](../AgentHarness/README.md),
[RepoKit](../RepoKit/README.md) and [RepoKit.AzureDevOps](../RepoKit.AzureDevOps/README.md).

## Run it

Prerequisites: the .NET 10 SDK, git, and network access to nuget.org the first time (to install Stryker). For Azure
DevOps repos, you also need a credential: `ADO_PAT`, or `az login` (see
[RepoKit.AzureDevOps](../RepoKit.AzureDevOps/README.md#where-the-credential-comes-from)).

```json
{
  "AzureDevOps": { "OrganizationUrl": "https://dev.azure.com/contoso", "Project": "Team" },
  "Repos": [
    {
      "Name": "payments-api",
      "Solution": "Payments.slnx",
      "Targets": [
        {
          "Project": "src/Payments.Core/Payments.Core.csproj",
          "TestProjects": [ "tests/Payments.Core.Tests/Payments.Core.Tests.csproj" ],
          "TestFilter": "FullyQualifiedName!~Slow",
          "Mutate": [ "!**/Migrations/**" ],
          "IgnoreStringMutationsIn": [ "**/Prompts/**" ]
        }
      ]
    },
    { "Name": "local-demo", "Path": "../demo", "Solution": "Demo.sln", "Targets": [ ] }
  ],
  "Stryker": { "Concurrency": 4 }
}
```

```sh
dotnet run --project src/TestHardener -- survey --config test-hardener.json
dotnet run --project src/TestHardener -- survey --config test-hardener.json --only payments-api
dotnet run --project src/TestHardener -- survey --config test-hardener.json --from out/run-20260927-101500
```

- **Repos:** a repo is either Azure DevOps (`Name`, with `OrganizationUrl` and `Project` from the defaults or its
  own) or local (`Path`, relative to the config file). Local repos are cloned too, so only committed files count.
- **Targets:** one per source project to mutate, each with the **fast** test projects to run against it. Leave out
  slow suites such as Aspire integration tests: Stryker runs the covering tests once per mutant. `TestFilter` is a
  `dotnet test --filter` expression and works only with the VSTest runner. On a repo whose `global.json` selects
  Microsoft.Testing.Platform, Stryker ignores filters silently: a target with a `TestFilter` fails, and `harden`
  refuses the repo before calling the agent, because its kill check depends on a filter.
- **`Mutate`:** Stryker `mutate` globs, with a leading `!` to exclude.
- **`IgnoreStringMutationsIn`:** globs where string mutants aren't worth a test, such as prompt or message text.
- **`--from`:** reuses an earlier run's Stryker reports instead of running Stryker again, which can take half an
  hour. The clone checks out the commit that run surveyed, so the report still matches the code. `survey.json`
  records which clone produced each report, so a `--from` of a `--from` run works too. A report whose file paths
  match nothing in the clone fails its target, rather than quietly finding no candidates.
- **Settings layer:** `appsettings.json`, then `--config`, then user secrets, then `TESTHARDENER_` environment
  variables.

Exit codes: 0 when at least one repo was surveyed, 2 for configuration, sign-in or Stryker install problems, 3 when
every repo failed, 4 when Copilot isn't ready or its quota is used up, 130 when cancelled.

## What it does

For each repo, one at a time:

1. **Clone** into a temporary folder with RepoKit, and record the commit.
2. **Restore** the solution once.
3. **For each target**, run Stryker in project mode from the source project's folder. The config file and every
   report go to the run's output folder, not into the clone. A failed target is reported, and the other targets
   still run.
4. **Read the report and pick the candidates.** A candidate is a surviving mutant that isn't one of these:
   - **Static data:** mutants in static initializers, such as word lists and lookup tables. Stryker marks them
     `static` and runs them against every test, so they have no covering tests.
   - **Logging:** inside a call to a `Log*` method or an `Activity` API, in the condition of an `if` whose only
     statements are such calls (and which has no `else`), or in a member named `Log*` or marked `[LoggerMessage]`.
     Tests for these would only check what gets logged.
   - **Outside a member:** for example an instance field initializer.
   - **An ignored string:** a string mutant in `IgnoreStringMutationsIn`.
5. **Group** the candidates by the method, constructor, accessor or operator they're in. Local functions and lambdas
   count as part of their method.
6. **Rank** the groups:
   1. the number of survivors that aren't string mutations;
   2. then all survivors;
   3. then how many `fix:`, `hotfix:`, `bugfix:` and `Revert` commits touched the file in the last `Hardening:FixHistoryDays`
      (180).

   Within a group, string mutants come last.

`Hardening:MaxGroupsPerRun` (5) sets how many groups the report shows as "next".

### How Stryker is run

- **The tool:**
  - `Stryker:Version` (5.0.0) is installed as a dotnet tool into `Stryker:ToolPath`
    (`~/.cache/test-hardener/tools`), and never into the repo's tool manifest.
  - Before each run, the installed version is checked with `dotnet tool list --tool-path`. `dotnet-stryker --version`
    doesn't report the version; it sets one.
  - The executable is run directly, since `dotnet stryker` doesn't look in a tool path.
- **The settings:** `coverage-analysis: perTest`, the `json` and `html` reporters, `Stryker:MutationLevel`
  (Standard), and `Stryker:IgnoreMethods`.
  - `IgnoreMethods` defaults to `*Log*`, `*Exception.ctor`, `ToString`, `GetHashCode`, `Task.Delay` and
    `*Timeout*`.
  - Setting `IgnoreMethods` replaces the default list.
- **How long and how many at once:** set `Stryker:Concurrency` when memory is short; each test session is its own
  process. `Stryker:TimeoutMinutesPerTarget` (90) stops a run that hangs.
- **Checking the result:** exit code 0 isn't enough. Stryker also exits with 0 when it finds no test project, so a
  run fails unless the report exists and lists both mutants and tests.

### Secrets

Restore and Stryker run the repo's build and tests on your machine. They get an environment without secrets:
- every variable AgentHarness's `AgentEnvironment.IsSecret` matches, such as `*_PAT`, `*TOKEN*`, `AZURE_*` and
  `*PASSWORD*`;
- the Azure DevOps credential variables.

A private NuGet feed that needs such a variable to restore won't work yet.

These processes, and the agent's own `dotnet build`/`dotnet test`, still run as you, with your file system and
network: nothing is sandboxed. The static checks keep agent-written tests away from files, the network and the
environment before anything runs them, but they are checks on source text, not a sandbox. Only point TestHardener
at repos your team owns.

## Harden: agent-written tests

```sh
dotnet run --project src/TestHardener -- harden --config test-hardener.json --only payments-api
dotnet run --project src/TestHardener -- harden --dry-run --config test-hardener.json --only payments-api
dotnet run --project src/TestHardener -- harden --dry-run --config test-hardener.json --from out/run-20260927-101500
```

**Prerequisites:** a GitHub Copilot subscription with the Copilot CLI signed in (`copilot`, then `/login`).

**Settings:**

| Setting | Default | What it does |
|---|---|---|
| `Hardening:MaxGroupsPerRun` | 5 | Groups worked on per repo |
| `Hardening:MaxSurvivorsPerGroup` | 12 | Survivors given to the agent per group; non-string ones first |
| `Hardening:MaxRounds` | 3 | Feedback rounds per group |
| `Hardening:OriginalRuns` | 5 | Times the new tests must pass on the current code |
| `Hardening:TestTimeoutMinutes` | 10 | Limit for one filtered test run (the final unfiltered run gets three times this) |
| `Agent:Model` | none | Pins a model; otherwise Copilot chooses |
| `Agent:MaxMinutes`, `MaxToolCalls`, `MaxRefusals` | 30, 90, 5 | Limits for one group's session, across all its rounds |
| `Agent:MaxAiCreditsPerRun` | 0 (no cap) | Once reached, the remaining groups are skipped |
| `ConventionFiles` (per repo) | `AGENTS.md`, `CLAUDE.md`, `.github/copilot-instructions.md` | Files the agent is told to read for testing conventions, if they exist |
| `TestNamePattern` (per repo) | none | A regex every new test name must match |
| `VerifyTestProjects` (per repo) | the targets' test projects | Test projects run once at the end, unfiltered |

It surveys first (or reuses `--from`), then for each repo takes the top groups across its targets and for each group:

1. **Chooses the one test file the agent may write**, in this order:
   1. the file that holds most of the tests Stryker says cover the method;
   2. a test file in the target's test projects that mentions the class, preferring the folder that mirrors the
      source file;
   3. a new `<Class>Tests.cs` at the mirrored path.

   For a new file, the app writes an empty class (namespace from the test project's `RootNamespace` and the
   folder, plus a `using` for the source namespace) and builds it first.
2. **Starts one agent session** in the clone. The session:
   - may read anything and run read-only commands;
   - may run `dotnet build`/`dotnet test`, but only on the target's test projects inside the clone, with
     `--no-restore` or `--no-build`, and without `--logger`, `--results-directory` or `-p:`;
   - may run `dotnet test` only while its test file passes the static checks below, so its own test code never runs
     before it has been checked;
   - may write only that one file;
   - has no Azure DevOps credential in its environment.

   The prompt has the method's source, the survivors (original and mutated code), the test file, the covering
   tests and the rules.
3. **Checks the result with code**, in this order. The first failure goes back to the agent, for up to
   `MaxRounds` rounds.
   1. **Nothing else changed.** Any other change is undone. A `TestResults` folder is deleted first.
   2. **The file only grew.** Checked with Roslyn:
      - existing members, attributes, fields, helpers and usings are unchanged, except that an existing
        `[Theory]` may gain `[InlineData]` rows;
      - no new top-level types (so nothing can shadow `Assert` or a helper), no extension methods, no duplicate
        members;
      - no added preprocessor directives, assembly attributes, or `global`, `static` or alias usings.
   3. **Each new test is sound.**
      - It has an assertion, isn't skipped, adds no comments, and its name matches `TestNamePattern`.
      - New code uses none of: reflection, the network, processes, the file system, the environment (except
        `Environment.NewLine`), sleeps, the real clock or randomness.
   4. **The test project builds.**
   5. **The new tests pass on the current code** `OriginalRuns` times in a row.
      - Each new test must actually have run.
      - A run is stopped after `Hardening:TestTimeoutMinutes` (10), and a hang goes back to the agent.
   6. **Stryker confirms the kills.** It re-runs with `mutate` narrowed to the method's character span, only the new
      tests (`test-case-filter`) and `disable-bail`. A survivor counts as killed only when Stryker reports `Killed`
      and a new test is among its killers. Every new test must kill at least one survivor. `NoCoverage` goes back
      as "your tests don't execute this code".
   7. **The tests changed no other files while they ran.** Anything they wrote is undone and the round fails.
4. **Keeps the change** if every check passed. Otherwise the file goes back to how it was before this group.

After the groups, `VerifyTestProjects` are built and run once. The kept files are written to `hardening.patch`.
The agent's summary of each test is reported as its claim, next to what was verified. The summary also records
`BlockedBy`, for when a test needed a helper in another file or a production refactor.

### Publishing

For an Azure DevOps repo, `harden`:

1. **Skips the repo before surveying it** if a pull request from `Publish:BranchPrefix` (`agent/test-hardening-`) is
   open, or was opened in the last `Publish:CooldownDays` (7), whatever happened to it.
2. **Lists pushed prefix branches that have no pull request** in the report. These are usually left by a run that
   stopped between push and PR. They aren't deleted.
3. **Works on a new branch per run** (`agent/test-hardening-<run>`).
4. **Asks you** (`Go ahead?`) once the tests have passed every check and the final run, unless you pass `--yes`.
   Without a console and without `--yes`, nothing is pushed.
5. **Commits only the kept test files.**
   - The commit is `test(<scopes>): cover surviving mutants`, where the scopes come from each target's `CommitScope`
     (for example `"CommitScope": "engine"`). Without scopes it's `test: …`.
   - The committer is `Publish:CommitName`/`CommitEmail`, unless git has an identity.
6. **Pushes and opens a draft pull request** labelled `Publish:Label` (`agent-generated`). The description:
   - says what was checked, and warns that the tests are agent-written, so the reviewer should read each assertion;
   - has a table of method, test, and the mutants each test catches;
   - has the mutation score of the touched files before and after, computed from the survey and the scoped runs;
   - has the agent's description of each test, escaped and with `@` mentions defused.

Local repos (`Path`) never push; the tests are saved as `hardening.patch`.

| Status | Meaning |
|---|---|
| Opened | A draft pull request is open; the report links it. |
| Ready | The tests passed; dry run or local repo, so they're only a patch. |
| Declined | You said no; the patch is saved. |
| Rejected | No group passed, or the final run of `VerifyTestProjects` failed. |
| Skipped | A pull request is open or recent. |
| NothingToDo | No survivors worth a test. |
| Failed | The survey, the agent or pushing failed. |

A group takes a few minutes: each round is an agent turn, a build, `OriginalRuns` filtered test runs, and a scoped
Stryker run (about 50 seconds on a 10k-line project).

## Output

Written to `out/run-<utc>/`:

| File | Holds |
|---|---|
| `report.md` | A table per target, then for each target: the score, what was skipped, the next groups with their survivors (original → mutated code), all groups, members Stryker couldn't mutate, and untested files. |
| `report.json` | The same, for tools. |
| `<repo>/survey.json` | One repo's result, including the commit and the clone path. `--from` reads it. |
| `<repo>/stryker/<target>/` | The Stryker config, `stryker.log`, and Stryker's own `reports/mutation-report.json` and `.html`. |
| `harden-report.md` / `.json` | `harden` only. Per repo: status and patch. Per group: outcome, the test file, the checks, the mutants each test kills, the agent's account, and why a group wasn't kept. |
| `<repo>/harden.json`, `<repo>/hardening.patch` | One repo's hardening result, and the kept tests as a patch. |
| `<repo>/groups/<n>/` | `agent.log`, `result.json`, and per round the test runs and the Stryker re-run. |

"Not mutated" lists members where every mutant was a compile error. When one mutant in a method doesn't compile,
Stryker's safe mode drops every mutant in that method, so those methods have no survivors to report.

## Cost

A full Stryker run on a 10k-line project with about 1,800 fast tests took 27 minutes at concurrency 4, and a 4k-line
project took 4 minutes. Use `--from` to iterate on the analysis without running Stryker again.
