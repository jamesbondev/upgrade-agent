using UpgradeAgent.Config;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.MsBuild;

namespace UpgradeAgent.Run;

internal abstract record CommitResult
{
    public sealed record Committed(string Sha) : CommitResult;

    public sealed record Refused(string Reason) : CommitResult;
}

internal sealed class GroupCommitter(GitCli git, TargetOptions target)
{
    private const int HookOutputLines = 6;

    public async Task<CommitResult> CommitAsync(string worktree, string message, CancellationToken cancellationToken)
    {
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
