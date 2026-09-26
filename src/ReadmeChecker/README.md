# ReadmeChecker

Clones the repos you list and reports the READMEs that no longer match their repo: broken links, commands and paths
that point at things that are gone, versions that don't match the projects, and new projects the README doesn't
mention. `check` only reports; `fix` has an agent correct each stale README, checks the change with a script, and
opens a draft pull request in Azure DevOps. It is built on [AgentHarness](../AgentHarness/README.md), [RepoKit](../RepoKit/README.md)
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

Exit codes: 0 when at least one repo was checked, 2 for configuration, sign-in or Azure DevOps problems, 3 when every
repo failed, 4 when Copilot isn't ready, 130 when cancelled.

## Deep mode: check every claim

**Status: experimental.** The one real benchmark so far ran on a fallback model (claude-haiku-4.5) and ran out of
Copilot quota part-way: it found one real problem with proof and no false positives, at about 15 AI credits per part.
Check which model Copilot actually serves (the report notes fallbacks) before relying on it.

The default check confirms the script's signals and looks for obvious gaps. It catches broken references well, but not
a README that names the wrong model, the wrong queue names or a feature that was removed. `--deep` (or
`Readme:Depth: Deep`, or `"Depth": "Deep"` on one repo) checks the README claim by claim:

```sh
dotnet run --project src/ReadmeChecker -- check --config readme-checker.json --only payments-api --deep
```

1. The README is split at `#`/`##` headings into parts of about 80 lines, never inside a code block or a list, and
   at most 8 parts (the rest is reported as not checked).
2. Each part gets its own read-only session. It lists every concrete claim (names, counts, values, versions, lists,
   behaviour) and checks each against source and config files, treating docs and tests as possibly stale too. It
   answers even when it hits its limits, so what it confirmed before that is kept ("partly checked").
3. A new kind of issue, `WrongClaim`, needs proof the script can check:
   - a `Truth` (what the code says) and an `EvidenceQuote` copied from a cited file. The quote must really be in that
     file, which must be source or config (not Markdown, not only under a test folder, at most 1 MB, and not a file
     the agent isn't allowed to read), at least 12 characters long, and share a name or value with the `Truth`; or
   - a `MissingTerm` for something that no longer exists: the README must contain it and `git grep` must find it
     nowhere outside Markdown.

   In deep mode every other kind of problem needs the same proof too, except broken references the script found and
   missing list entries with evidence. The README quote must lie in the part being checked.
4. The result is `Stale` if any problem survives, `Current` if every part was fully checked and none did, and
   `Unsure` otherwise. The report shows each problem's line, the truth and its evidence, and per part: status,
   claims checked and AI credits.

Deep mode ignores `Agent:SkipWhenClean`: a README with no signals is exactly what it is for. It costs one session per
part (argus's 287-line README is 5 parts), so pin `Agent:Model` and set `Agent:MaxAiCreditsPerRun`; the remaining
budget is passed down, and parts past it are skipped.

The quote check stops invented evidence, not misreading: a count ("two producers") is backed by one real example of
the third, not proven exhaustive. When `fix` changes a wrong claim, the new text must contain what the code says (a
name or value shared by the truth and its evidence), or the fix is rejected; a term that no longer exists must be
gone. Claims the fix left alone are listed in the pull request under "Left unchanged".

## Fix and open pull requests

```sh
dotnet run --project src/ReadmeChecker -- fix --config readme-checker.json --only payments-api --dry-run
dotnet run --project src/ReadmeChecker -- fix --config readme-checker.json --only payments-api
```

`fix` runs the same check first, then for each repo that is `Stale` with at least one confirmed issue or broken link:

1. **Skips the repo** if a pull request from a `Publish:BranchPrefix` (`agent/readme-refresh-`) branch is still open,
   or was opened in the last `Publish:CooldownDays` (30), whatever happened to it. Each run uses a new branch
   (`agent/readme-refresh-<run>`), so a merged or abandoned branch never blocks a later run.
2. **Has the agent edit the README.** The session can read, run read-only commands, and write only the README. It is
   given the confirmed issues and broken links and told to fix only those, keep the structure and tone, and invent
   nothing.
3. **Checks the change with a script.** It is rejected if:
   - any other file changed, or the README didn't change
   - fewer than `Readme:MinKeptRatio` (half) of the README's original lines are left
   - it refers to a file, folder or command target that isn't in the repository and wasn't already in the README
   - a broken link is still there
   - it links to a site that the README and the repository don't already mention
4. **Asks you** (`Go ahead?`) before pushing, unless you pass `--yes`. Without a console and without `--yes`, nothing is
   pushed.
5. **Commits only the README** (as your git identity, or `Publish:CommitName`/`CommitEmail` when there is none),
   pushes the branch, and opens a **draft** pull request labelled `Publish:Label` (`agent-generated`) against the
   default branch. The description lists what was out of date, the agent's own account of its changes (escaped, with
   `@` mentions defused) and says the text is unverified.

`--dry-run` does steps 2 and 3 and pushes nothing. Local repos (`Path`) never push: the change is saved as a patch.
Either way the run writes `out/run-<utc>/fix-report.md`, `fix-report.json`, and per repo `fix.json`, `readme.patch`
and `fix-agent.log`.

| Result | Meaning |
|---|---|
| Opened | A draft pull request is open; the report links it. |
| Ready | The fix passed the checks; dry run or local repo, so it's only a patch. |
| Declined | You said no; the patch is saved. |
| Rejected | The fix failed the checks; the report says which. |
| Failed | The agent failed or stopped, or pushing or opening the pull request failed. |
| Skipped | A pull request is open or recent. |
| NothingToFix | The README isn't stale, or there was nothing specific to fix. |

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
   - Paths with a folder in them, in inline code and shell code blocks, and the targets of `cd`,
     `dotnet run/test/build --project`, `./script` and `pwsh script.ps1`. `cd` is followed, and folders the block
     creates (`dotnet new -o`, `mkdir`) don't count. URLs, placeholders, globs, absolute paths, gitignored and
     build-output paths are skipped, and so are bare file names in prose (`secrets.json` usually means a kind of file,
     not one in this repo). What each command means lives in `Detection/CommandRules.cs`: a rule reads a command's
     tokens and says which are targets, which folders it creates or moves into, and whether it leaves the repo.
     `CommandScanner` acts on that and checks the paths. To teach it a new command, add a rule and a
     `CommandRulesTests` case.
   - Type names (`IReviewEngine`, `ReviewJobHandler`) that no code or config file contains. Names inside
     language-tagged code examples are skipped, since those are often other libraries' APIs.
   - `.NET 6`, `net6.0` and SDK versions that the projects and `global.json` don't use ("or later" is fine).
   - Files missing from a list: when the README links at least three files in a folder and most of the folder, the
     folder's other files of that type (not `index`, `README` or templates) are candidates.
   - Projects the README doesn't name: under a top-level folder where it names at least half of the projects
     (test projects only if it names a test project there), or non-test projects added since the README last
     changed. A README in a subfolder only covers projects under that folder.

   Only the first `Readme:MaxCandidates` (25) are kept, certain ones first.
5. **Skip the agent when there is nothing to check**: no signals, and the README changed within the last
   `Readme:RecentCommits` (20) commits, gives `Current (no agent)`.
6. **Ask the agent.** A read-only Copilot session gets the README, the facts and the signals, explores the repo to
   confirm or dismiss each signal and find what's missing, then answers with a verdict and issues. Every issue must
   quote the README exactly and cite files that exist, or it is dropped (the report lists what was dropped and why).
   A broken reference with no evidence must match a broken link or a missing path with a folder in it that the
   script also found: a bare file name in prose (`AGENTS.md`, `secrets.json`) often means a file in the repositories
   a tool works on, not in this one.
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
`git ls-files` before it is reported. `check` writes nothing back to any repo. In `fix`, the write session can change
only the README, the script checks the result, only the README is committed, a person approves each push, and the
pull request is a draft. The branch is pushed with your Azure DevOps credential, which the agent never sees.
