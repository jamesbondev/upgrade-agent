using Microsoft.TeamFoundation.SourceControl.WebApi;
using UpgradeAgent.Config;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;

namespace UpgradeAgent.Publishing;

/// <summary>
/// Pushes the verified branch and opens a draft PR. The credential is acquired when it's needed (after the
/// agent sessions) and dropped afterwards; nothing about it reaches the agent's environment.
/// </summary>
internal sealed class AzureDevOpsPublisher(AzureDevOpsOptions options, IProcessRunner processRunner)
{
    public string Destination => $"{options.OrganizationUrl.TrimEnd('/')}/{options.Project}/_git/{options.Repository}";

    /// <summary>Fails fast, before any agent time is spent, if the credential or repository is wrong.</summary>
    public async Task<string> VerifyAsync(CancellationToken cancellationToken)
    {
        var credential = await AzureDevOpsCredential.AcquireAsync(options, cancellationToken);
        using var client = new AzureDevOpsClient(options.OrganizationUrl, credential);
        var repository = await client.GetRepositoryAsync(options.Project, options.Repository, cancellationToken);
        return $"{options.Project}/{repository.Name} (default branch {repository.DefaultBranch?.Replace("refs/heads/", "", StringComparison.Ordinal) ?? "none: push the repo first"}) via {credential.Source}";
    }

    public async Task<PushResult> PushAsync(RunReport report, CancellationToken cancellationToken)
    {
        var credential = await AzureDevOpsCredential.AcquireAsync(options, cancellationToken);
        using var client = new AzureDevOpsClient(options.OrganizationUrl, credential);
        var repository = await client.GetRepositoryAsync(options.Project, options.Repository, cancellationToken);

        var result = await new GitPush(processRunner).PushAsync(report.WorktreePath, repository.RemoteUrl, report.Branch, credential, cancellationToken);
        return result.Succeeded
            ? new PushResult(true, false, $"Pushed {report.Branch} ({report.Ledger.Count} verified commit(s)) to {GitPush.CleanUrl(repository.RemoteUrl)}.")
            : new PushResult(false, false, $"Push failed (exit {result.ExitCode}): {result.CombinedOutput.Trim()}");
    }

    public async Task<string> CreatePullRequestAsync(RunReport report, string title, string description, CancellationToken cancellationToken)
    {
        var credential = await AzureDevOpsCredential.AcquireAsync(options, cancellationToken);
        using var client = new AzureDevOpsClient(options.OrganizationUrl, credential);
        var repository = await client.GetRepositoryAsync(options.Project, options.Repository, cancellationToken);
        var pullRequest = await client.CreateDraftPullRequestAsync(repository, report.Branch, title, description, options.Label, cancellationToken);
        return AzureDevOpsClient.WebUrl(repository, pullRequest);
    }

    /// <summary>One-time demo setup: pushes the target repo's HEAD as <paramref name="branch"/>, only into an empty repository.</summary>
    public async Task<string> SeedAsync(string repoPath, string branch, CancellationToken cancellationToken)
    {
        var credential = await AzureDevOpsCredential.AcquireAsync(options, cancellationToken);
        using var client = new AzureDevOpsClient(options.OrganizationUrl, credential);
        var repository = await client.GetRepositoryAsync(options.Project, options.Repository, cancellationToken);
        if (repository.DefaultBranch is not null || (repository.Size ?? 0) > 0)
        {
            return $"{Destination} already has content (default branch {repository.DefaultBranch}); nothing pushed.";
        }

        var result = await new GitPush(processRunner).PushAsync(repoPath, repository.RemoteUrl, branch, credential, cancellationToken);
        return result.Succeeded
            ? $"Seeded {Destination} with {branch}."
            : throw new InvalidOperationException($"Seeding failed (exit {result.ExitCode}): {result.CombinedOutput.Trim()}");
    }

    /// <summary>What a demo reset would remove: active labelled PRs from agent branches, and the agent branches.</summary>
    public async Task<(IReadOnlyList<GitPullRequest> PullRequests, IReadOnlyList<GitRef> Branches, Func<CancellationToken, Task> Apply)> PlanCleanupAsync(CancellationToken cancellationToken)
    {
        var credential = await AzureDevOpsCredential.AcquireAsync(options, cancellationToken);
        var client = new AzureDevOpsClient(options.OrganizationUrl, credential);
        var repository = await client.GetRepositoryAsync(options.Project, options.Repository, cancellationToken);
        var pullRequests = await client.FindAgentPullRequestsAsync(repository, options.Label, cancellationToken);
        var branches = await client.FindAgentBranchesAsync(repository, cancellationToken);

        async Task Apply(CancellationToken token)
        {
            using (client)
            {
                foreach (var pullRequest in pullRequests)
                {
                    await client.AbandonAsync(repository, pullRequest, token);
                }

                foreach (var branch in branches)
                {
                    await client.DeleteBranchAsync(repository, branch, token);
                }
            }
        }

        return (pullRequests, branches, Apply);
    }
}
