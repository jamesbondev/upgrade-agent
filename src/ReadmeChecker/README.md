# ReadmeChecker

Clones the repos you list and reports the READMEs that no longer match their repo: broken links, commands and paths
that point at things that are gone, versions that don't match the projects, and new projects the README doesn't
mention. It changes nothing. It is built on [AgentHarness](../AgentHarness/README.md), [RepoKit](../RepoKit/README.md)
and [RepoKit.AzureDevOps](../RepoKit.AzureDevOps/README.md).

## Run it

Prerequisites: .NET 10 SDK, git, and for the agent a GitHub Copilot subscription with the Copilot CLI signed in
(`copilot`, then `/login`). For Azure DevOps repos, a credential (see
[RepoKit.AzureDevOps](../RepoKit.AzureDevOps/README.md#where-the-credential-comes-from)): `ADO_PAT`, or `az login`.

```json
{
  "AzureDevOps": { "OrganizationUrl": "https://dev.azure.com/contoso", "Project": "Team" },
  "Repos": [
    { "Name": "payments-api" },
    { "Name": "docs-site", "Project": "Other Team", "Readme": "docs/README.md" },
    { "Name": "local-demo", "Path": "../some/local/repo" }
  ],
  "Agent": { "Model": "claude-sonnet-4.5" }
}
```

```sh
dotnet run --project src/ReadmeChecker -- check --config readme-checker.json
dotnet run --project src/ReadmeChecker -- check --config readme-checker.json --only payments-api --agent none
```

- A repo is either Azure DevOps (`Name`, with `OrganizationUrl` and `Project` from the defaults or its own) or local
  (`Path`, relative to the config file). Local repos are cloned too, so only committed files count.
- `--only` narrows the run; `--agent none` reports the script's signals without calling Copilot.
- Settings layer: `appsettings.json`, then `--config`, then user secrets, then `READMECHECKER_` environment
  variables (e.g. `READMECHECKER_Agent__Model`).

The run prints one line per repo and a summary table, and writes `out/run-<utc>/`:

| File | Holds |
|---|---|
| `report.md` | The summary table, then each repo: verdict, the agent's issues, the script's signals, dropped issues. |
| `report.json` | The same, for tools. |
| `<repo>/assessment.json` | One repo's result. |
| `<repo>/agent.log` | Every agent event: messages, tool calls and their output. |

Exit codes: 0 when at least one repo was checked, 2 for configuration or sign-in problems, 3 when every repo failed,
4 when Copilot isn't ready, 130 when cancelled.

## How it decides

For each repo, one at a time:

1. **Clone** into a temporary folder with RepoKit (symlinks as plain files, credentials only in the environment).
   The clone is deleted afterwards unless `Output:KeepClones` is set.
2. **Find the README**: `Readme` if set, otherwise `README.md` at the root, whatever its case. No README gives
   `Missing`; a README that is a symbolic link gives `Error`.
3. **Collect facts** from git and the files: tracked files, projects, target frameworks (from projects and
   `Directory.Build.props`/`.targets`), the SDK in `global.json`, and what changed since the README last changed.
4. **Find signals** with a script. Existence is checked against `git ls-files`, so it is case-sensitive like the repo.
   - Broken relative links, including `<img src>`. These are certain.
   - Paths in inline code and in shell code blocks, and the targets of `cd`, `dotnet run/test/build --project`,
     `./script` and `pwsh script.ps1`. `cd` is followed, and folders the block creates (`dotnet new -o`, `mkdir`)
     don't count. URLs, placeholders, globs, absolute paths, gitignored and build-output paths are skipped.
   - `.NET 6`, `net6.0` and SDK versions that the projects and `global.json` don't use ("or later" is fine).
   - Non-test projects added since the README last changed that it doesn't name.

   Only the first `Readme:MaxCandidates` (25) are kept, certain ones first.
5. **Skip the agent when there is nothing to check**: no signals, and the README changed within the last
   `Readme:RecentCommits` (20) commits, gives `Current (no agent)`.
6. **Ask the agent.** A read-only Copilot session gets the README, the facts and the signals, explores the repo to
   confirm or dismiss each signal and find what's missing, then answers with a verdict and issues. Every issue must
   quote the README exactly and cite files that exist, or it is dropped (the report lists what was dropped and why).
7. **Combine**: certain signals make a repo `Stale` whatever the agent says; otherwise the agent's verdict stands,
   and `Stale` with no surviving issue becomes `Unsure`. If the agent fails or is stopped, the repo is `Unsure`.

| Verdict | Meaning |
|---|---|
| Stale | Something in the README is wrong or missing. |
| Unsure | The agent couldn't tell, failed, or its issues didn't hold up. |
| Missing | No README. |
| Error | The repo couldn't be checked (clone failed, README is a link). The rest of the run carries on. |
| Unassessed | Signals only, because the agent was off or the credit cap was reached. |
| Current | Nothing found. "(no agent)" means the script decided. |

## Cost and limits

Each assessed repo is one Copilot session: `Agent:MaxMinutes` (5), `Agent:MaxToolCalls` (40), `Agent:MaxRefusals`
(5). A check of this repo's two READMEs used about 4.7 AI credits. Set `Agent:Model` to pin a model (otherwise
Copilot chooses, and it may differ per repo), and `Agent:MaxAiCreditsPerRun` to stop calling the agent once a run
has spent that much; the remaining repos get signals only.

The README and the repo's files are sent to Copilot. Only list repos where that is acceptable.

## Security

The agent works in the clone with a read-only policy (AgentHarness `WorkspacePolicy`: reads and read-only commands
inside the clone, nothing else), and its environment has no Azure DevOps credential. Everything it reads is treated as
data: the prompt says so, the README is passed inside tags, and its answer is checked against the README and
`git ls-files` before it is reported. Nothing is written back to any repo.
