using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Run;

/// <param name="Commit">The new commit, or null when the commit was refused (see <paramref name="Error"/>).</param>
internal sealed record CommitResult(string? Commit, string? Error);

/// <summary>
/// Commits a verified group. Repository git hooks run by default: a company's pre-commit checks (for
/// example ggshield secret scanning) apply to agent commits exactly as to human ones. A hook that fails
/// rejects the group. A hook that rewrites files is also a rejection, because what gets committed must be
/// exactly the tree the guardrails verified.
/// </summary>
internal sealed class GroupCommitter(GitCli git, bool runHooks)
{
    public async Task<CommitResult> CommitAsync(string worktree, string startCommit, string message, CancellationToken cancellationToken)
    {
        // Tracked changes plus new source files only. Anything else new is dropped, never committed blindly.
        await git.RunAsync(worktree, ["add", "--update"], cancellationToken);
        var newSources = (await git.ListUntrackedAsync(worktree, cancellationToken))
            .Where(MsBuildFiles.IsSourceFile)
            .ToList();
        if (newSources.Count > 0)
        {
            await git.RunAsync(worktree, ["add", "--", .. newSources], cancellationToken);
        }

        var verifiedTree = (await git.RunAsync(worktree, ["write-tree"], cancellationToken)).Trim();

        List<string> arguments = await git.HasIdentityAsync(worktree, cancellationToken)
            ? []
            : ["-c", "user.name=UpgradeAgent", "-c", "user.email=upgrade-agent@localhost"];
        arguments.AddRange(["commit", "-q", "-m", message]);
        if (!runHooks)
        {
            arguments.Add("--no-verify");
        }

        var result = await git.TryRunAsync(worktree, arguments, cancellationToken);
        if (!result.Succeeded)
        {
            return new CommitResult(null, $"git commit was refused (a pre-commit or commit-msg hook?): {string.Join(" | ", result.CombinedOutput.TailLines(6))}");
        }

        var committedTree = (await git.RunAsync(worktree, ["rev-parse", "HEAD^{tree}"], cancellationToken)).Trim();
        if (committedTree != verifiedTree)
        {
            await git.RunAsync(worktree, ["reset", "--hard", "-q", startCommit], CancellationToken.None);
            return new CommitResult(null, "a git hook changed the committed files after verification, so the commit was undone");
        }

        await git.RunAsync(worktree, ["clean", "-fd", "-q"], cancellationToken);
        return new CommitResult(await git.HeadAsync(worktree, cancellationToken), null);
    }
}
