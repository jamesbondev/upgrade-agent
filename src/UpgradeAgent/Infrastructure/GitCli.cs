namespace UpgradeAgent.Infrastructure;

internal sealed class GitException(string message, string output) : Exception($"{message}{Environment.NewLine}{output}".TrimEnd());

/// <summary>Thin wrapper over the git CLI. Never prompts: a hidden credential dialog would hang a live demo.</summary>
internal sealed class GitCli(IProcessRunner processRunner)
{
    private static readonly Dictionary<string, string?> NonInteractiveEnvironment = new()
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        ["GIT_OPTIONAL_LOCKS"] = "0",
    };

    public async Task<string> RunAsync(string repoPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default)
    {
        var result = await TryRunAsync(repoPath, arguments, cancellationToken);
        return result.Succeeded
            ? result.StandardOutput
            : throw new GitException($"git {string.Join(' ', arguments)} failed with exit code {result.ExitCode}.", result.CombinedOutput);
    }

    public Task<ProcessResult> TryRunAsync(string repoPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken = default) =>
        processRunner.RunAsync("git", ["-c", "core.quotepath=false", .. arguments], repoPath, NonInteractiveEnvironment, cancellationToken);

    public async Task<string> HeadAsync(string repoPath, CancellationToken cancellationToken = default) =>
        (await RunAsync(repoPath, ["rev-parse", "HEAD"], cancellationToken)).Trim();

    public async Task<IReadOnlyList<string>> StatusAsync(string repoPath, bool includeIgnored, CancellationToken cancellationToken = default)
    {
        List<string> arguments = ["status", "--porcelain=v1", "--untracked-files=all"];
        if (includeIgnored)
        {
            arguments.Add("--ignored");
        }

        return SplitLines(await RunAsync(repoPath, arguments, cancellationToken));
    }

    public async Task<IReadOnlyList<string>> ListFilesAsync(string repoPath, CancellationToken cancellationToken = default) =>
        SplitLines(await RunAsync(repoPath, ["ls-files"], cancellationToken));

    public async Task<IReadOnlyList<string>> ListUntrackedAsync(string repoPath, CancellationToken cancellationToken = default) =>
        SplitLines(await RunAsync(repoPath, ["ls-files", "--others", "--exclude-standard"], cancellationToken));

    /// <summary>Returns the file's content at <paramref name="commit"/>, or null if it didn't exist there.</summary>
    public async Task<string?> ShowFileAsync(string repoPath, string commit, string relativePath, CancellationToken cancellationToken = default)
    {
        var result = await TryRunAsync(repoPath, ["show", $"{commit}:{relativePath.Replace('\\', '/')}"], cancellationToken);
        return result.Succeeded ? result.StandardOutput : null;
    }

    public async Task<bool> BranchExistsAsync(string repoPath, string branch, CancellationToken cancellationToken = default) =>
        (await TryRunAsync(repoPath, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken)).Succeeded;

    public async Task<bool> HasIdentityAsync(string repoPath, CancellationToken cancellationToken = default) =>
        (await TryRunAsync(repoPath, ["config", "user.email"], cancellationToken)).Succeeded
        && (await TryRunAsync(repoPath, ["config", "user.name"], cancellationToken)).Succeeded;

    /// <summary>
    /// Puts the worktree back exactly at <paramref name="commit"/>: tracked changes reset, new files removed.
    /// Deliberately not cancellable: a half-applied change must never survive, even when the run is being cancelled.
    /// </summary>
    public async Task RevertToAsync(string worktree, string commit)
    {
        await RunAsync(worktree, ["reset", "--hard", "-q", commit], CancellationToken.None);
        await RunAsync(worktree, ["clean", "-fd", "-q"], CancellationToken.None);
    }

    internal static IReadOnlyList<string> SplitLines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
