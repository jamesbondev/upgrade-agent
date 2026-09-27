# TestHardener

Runs [Stryker.NET](https://stryker-mutator.io/docs/stryker-net/introduction/) on the repos you list and finds the
mutants their tests don't catch. Stryker makes small changes to the code (a mutant), such as `>` to `>=` or removing
a statement, and runs the tests. A mutant the tests don't notice (a survivor) marks behavior no test checks.

`survey` reports the survivors worth a test, grouped by method and ranked. It changes nothing. Having an agent write
the tests (`harden`) comes next. It is built on [RepoKit](../RepoKit/README.md) and
[RepoKit.AzureDevOps](../RepoKit.AzureDevOps/README.md).

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
  `dotnet test --filter` expression and works only with the VSTest runner. A repo whose `global.json` selects
  Microsoft.Testing.Platform is refused, because Stryker would ignore the filter silently.
- **`Mutate`:** Stryker `mutate` globs, with a leading `!` to exclude.
- **`IgnoreStringMutationsIn`:** globs where string mutants aren't worth a test, such as prompt or message text.
- **`--from`:** reuses an earlier run's Stryker reports instead of running Stryker again, which can take half an
  hour. The clone checks out the commit that run surveyed, so the report still matches the code.
- **Settings layer:** `appsettings.json`, then `--config`, then user secrets, then `TESTHARDENER_` environment
  variables.

Exit codes: 0 when at least one repo was surveyed, 2 for configuration, sign-in or Stryker install problems, 3 when
every repo failed, 130 when cancelled.

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
   - **Logging:** inside a call to a `Log*` method or an `Activity` API, or in a member named `Log*` or marked
     `[LoggerMessage]`.
   - **Outside a member:** for example an instance field initializer.
   - **An ignored string:** a string mutant in `IgnoreStringMutationsIn`.
5. **Group** the candidates by the method, constructor, accessor or operator they're in. Local functions and lambdas
   count as part of their method.
6. **Rank** the groups:
   1. the number of survivors that aren't string mutations;
   2. then all survivors;
   3. then how many `fix:`, `hotfix:` and `Revert` commits touched the file in the last `Hardening:FixHistoryDays`
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

## Output

Written to `out/run-<utc>/`:

| File | Holds |
|---|---|
| `report.md` | A table per target, then for each target: the score, what was skipped, the next groups with their survivors (original → mutated code), all groups, members Stryker couldn't mutate, and untested files. |
| `report.json` | The same, for tools. |
| `<repo>/survey.json` | One repo's result, including the commit and the clone path. `--from` reads it. |
| `<repo>/stryker/<target>/` | The Stryker config, `stryker.log`, and Stryker's own `reports/mutation-report.json` and `.html`. |

"Not mutated" lists members where every mutant was a compile error. When one mutant in a method doesn't compile,
Stryker's safe mode drops every mutant in that method, so those methods have no survivors to report.

## Cost

A full Stryker run on a 10k-line project with about 1,800 fast tests took 27 minutes at concurrency 4, and a 4k-line
project took 4 minutes. Use `--from` to iterate on the analysis without running Stryker again.
