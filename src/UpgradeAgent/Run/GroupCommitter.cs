using UpgradeAgent.Config;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.MsBuild;

namespace UpgradeAgent.Run;

internal abstract record CommitResult
{
    public sealed record Committed(string Sha) : CommitResult;

    /// <summary>Nothing was kept. The caller reverts the worktree.</summary>
    public sealed record Refused(string Reason) : CommitResult;
}

/// <summary>
/// Commits a verified group. Repository git hooks run by default: a company's pre-commit checks (for
/// example ggshield secret scanning) apply to agent commits exactly as to human ones. A hook that fails
/// rejects the group. A hook that rewrites files is also a rejection, because what gets committed must be
/// exactly the tree the guardrails verified.
/// </summary>
internal sealed class GroupCommitter(GitCli git, TargetOptions target)
{
    private const int HookOutputLines = 6;

    public async Task<CommitResult> CommitAsync(string worktree, string message, CancellationToken cancellationToken)
    {
        // Tracked changes plus new source files only. Anything else new is dropped, never committed blindly.
        await git.RunAsync(worktree, ["add", "--update"], cancellationToken);
        var newSources = (await git.ListUntrackedAsync(worktree, cancellationToken)).Where(MsBuildFiles.IsSourceFile).ToList();
        if (newSources.Count > 0)
        {
            await git.RunAsync(worktree, ["add", "--", .. newSources], cancellationToken);
        }

        var verifiedTree = (await git.RunAsync(worktree, ["write-tree"], cancellationToken)).Trim();

        List<string> arguments = await git.HasIdentityAsync(worktree, cancellationToken)
            ? []
            : ["-c", "user.name=UpgradeAgent", "-c", "user.email=upgrade-agent@localhost"];
        arguments.AddRange(["commit", "-q", "-m", message]);
        if (!target.RunGitHooks)
        {
            arguments.Add("--no-verify");
        }

        var result = await git.TryRunAsync(worktree, arguments, cancellationToken);
        if (!result.Succeeded)
        {
            return new CommitResult.Refused(
                $"git commit was refused (a pre-commit or commit-msg hook?): {string.Join(" | ", result.CombinedOutput.TailLines(HookOutputLines))}");
        }

        var committedTree = (await git.RunAsync(worktree, ["rev-parse", "HEAD^{tree}"], cancellationToken)).Trim();
        if (committedTree != verifiedTree)
        {
            return new CommitResult.Refused("a git hook changed the committed files after verification, so the commit was undone");
        }

        await git.RunAsync(worktree, ["clean", "-fd", "-q"], cancellationToken);
        return new CommitResult.Committed(await git.HeadAsync(worktree, cancellationToken));
    }
}
