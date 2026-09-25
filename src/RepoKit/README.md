# RepoKit

A small .NET 10 library for tools that work across many repos. It runs processes and git, and clones a repo into a
throwaway workspace that is safe to hand to an agent. It has no package dependencies and knows nothing about LLMs,
Azure DevOps or any app, so you can copy the folder into any solution and add a `ProjectReference`.

## Clone a repo, look around, clean up

```csharp
using RepoKit;

var git = new GitCli(new ProcessRunner());
var source = RepoSource.Remote("payments-api", "https://dev.azure.com/contoso/Team/_git/payments-api", authorizationHeader);
// or RepoSource.Local("demo", "../demo")

await using var workspace = await RepoWorkspace.CloneAsync(
    source, RepoWorkspace.DefaultWorkRoot("my-tool"), git, timeout: TimeSpan.FromMinutes(5), cancellationToken);

var files = await workspace.Git.ListFilesAsync(workspace.Path);
var readme = await workspace.Git.LastCommitTouchingAsync(workspace.Path, "README.md");
```

Disposing the workspace deletes the clone. Set `workspace.Keep = true` to keep it for debugging.

## What a clone is

`RepoWorkspace.CloneAsync` runs `git clone --single-branch --no-tags` of the default branch, with full history, and:

- **Symbolic links check out as plain text files** (`core.symlinks=false`). Policy path checks in AgentHarness don't
  follow links, so a link in an untrusted repo could otherwise point an agent outside the clone. `FileModeAsync`
  still reports `120000` for a link, if you want to refuse to act on one.
- **Files are byte-for-byte as committed** (`core.autocrlf=false`), so edits don't rewrite line endings.
- **Git LFS content isn't downloaded** (`GIT_LFS_SKIP_SMUDGE=1`).
- **Credentials reach git only through the environment.** The authorization header goes to git as a
  `GIT_CONFIG_*` variable scoped to the remote's host. It is never in the URL or in `.git/config`, where an agent
  reading the clone could find it. `workspace.Git` carries the same environment, so later fetches and pushes
  authenticate too.
- **Failures are one exception.** A failed clone, an empty repository and a timeout all throw `CloneException` with a
  `Reason`, and leave nothing behind. Your own cancellation throws `OperationCanceledException`.

Two repos with the same name get separate folders (`name`, `name-2`).

## GitCli

`GitCli` runs git without prompts (`GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`) and with paths unquoted.
`RunAsync` throws `GitException` with git's output; `TryRunAsync` returns the `ProcessResult`. The history helpers
take paths literally (no pathspec magic):

| Method | Returns |
|---|---|
| `ListFilesAsync` | Tracked files, as git spells them (case-sensitive). |
| `FileModeAsync` | The file's mode: `100644`, `100755`, `120000` for a link, or null. |
| `LastCommitTouchingAsync` | The last commit that changed a path, with its date. |
| `CommitCountSinceAsync` | Commits since a commit, leaving out ones that touch only the excluded paths. |
| `PathsChangedSinceAsync` | Paths changed since a commit; `diffFilter: "A"` for added only. |

`WithEnvironment` returns a copy that adds environment variables to every call.
