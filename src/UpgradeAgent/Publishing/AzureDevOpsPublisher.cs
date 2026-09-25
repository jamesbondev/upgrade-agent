using Microsoft.TeamFoundation.SourceControl.WebApi;
using UpgradeAgent.Config;
using UpgradeAgent.Run;

namespace UpgradeAgent.Publishing;

/// <summary>What a demo reset would remove: active labelled PRs from agent branches, and the agent branches.</summary>
internal sealed record CleanupPlan(IReadOnlyList<GitPullRequest> PullRequests, IReadOnlyList<GitRef> Branches)
{
    public bool IsEmpty => PullRequests.Count == 0 && Branches.Count == 0;
}

/// <summary>
/// Pushes the verified branch and opens a draft PR. The credential is acquired once, when first needed (after
/// the agent sessions), and nothing about it reaches the agent's environment.
/// </summary>
internal sealed class AzureDevOpsPublisher(AzureDevOpsOptions options, AzureDevOpsCredentialProvider credentials, GitPush gitPush) : IPushDestination, IDisposable
{
    private AzureDevOpsSession? _session;

    public string Destination => $"{options.OrganizationUrl.TrimEnd('/')}/{options.Project}/_git/{options.Repository}";

    public Task<string> DescribeAsync(CancellationToken cancellationToken) => Task.FromResult(Destination);

    /// <summary>Fails fast, before any agent time is spent, if the credential or repository is wrong.</summary>
    public async Task<string> VerifyAsync(CancellationToken cancellationToken)
    {
        var session = await ConnectAsync(cancellationToken);
        var defaultBranch = session.Repository.DefaultBranch?.Replace("refs/heads/", "", StringComparison.Ordinal) ?? "none: push the repo first";
        return $"{options.Project}/{session.Repository.Name} (default branch {defaultBranch}) via {session.Credential.Source}";
    }

    public async Task<PushResult> PushAsync(RunReport report, CancellationToken cancellationToken)
    {
        var session = await ConnectAsync(cancellationToken);
        var result = await gitPush.PushAsync(report.WorktreePath, session.Repository.RemoteUrl, report.Branch, session.Credential, cancellationToken);
        return result.Succeeded
            ? PushResult.Success($"Pushed {report.Branch} ({report.Ledger.Count} verified commit(s)) to {GitPush.CleanUrl(session.Repository.RemoteUrl)}.")
            : PushResult.Failed($"Push failed (exit {result.ExitCode}): {result.CombinedOutput.Trim()}");
    }

    public async Task<string> CreatePullRequestAsync(RunReport report, string title, string description, CancellationToken cancellationToken)
    {
        var session = await ConnectAsync(cancellationToken);
        var pullRequest = await session.Client.CreateDraftPullRequestAsync(session.Repository, report.Branch, title, description, options.Label, cancellationToken);
        return AzureDevOpsClient.WebUrl(session.Repository, pullRequest);
    }

    /// <summary>One-time demo setup: pushes the target repo's HEAD as <paramref name="branch"/>, only into an empty repository.</summary>
    public async Task<string> SeedAsync(string repoPath, string branch, CancellationToken cancellationToken)
    {
        var session = await ConnectAsync(cancellationToken);
        if (session.Repository.DefaultBranch is not null || (session.Repository.Size ?? 0) > 0)
        {
            return $"{Destination} already has content (default branch {session.Repository.DefaultBranch}); nothing pushed.";
        }

        var result = await gitPush.PushAsync(repoPath, session.Repository.RemoteUrl, branch, session.Credential, cancellationToken);
        return result.Succeeded
            ? $"Seeded {Destination} with {branch}."
            : throw new InvalidOperationException($"Seeding failed (exit {result.ExitCode}): {result.CombinedOutput.Trim()}");
    }

    public async Task<CleanupPlan> PlanCleanupAsync(CancellationToken cancellationToken)
    {
        var session = await ConnectAsync(cancellationToken);
        return new CleanupPlan(
            await session.Client.FindAgentPullRequestsAsync(session.Repository, options.Label, cancellationToken),
            await session.Client.FindAgentBranchesAsync(session.Repository, cancellationToken));
    }

    public async Task ApplyCleanupAsync(CleanupPlan plan, CancellationToken cancellationToken)
    {
        var session = await ConnectAsync(cancellationToken);
        foreach (var pullRequest in plan.PullRequests)
        {
            await session.Client.AbandonAsync(session.Repository, pullRequest, cancellationToken);
        }

        foreach (var branch in plan.Branches)
        {
            await session.Client.DeleteBranchAsync(session.Repository, branch, cancellationToken);
        }
    }

    public void Dispose() => _session?.Client.Dispose();

    private async Task<AzureDevOpsSession> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_session is not null)
        {
            return _session;
        }

        if (!options.IsConfigured)
        {
            throw new ConfigurationException("Azure DevOps needs AzureDevOps:OrganizationUrl, AzureDevOps:Project and AzureDevOps:Repository.");
        }

        var credential = await credentials.AcquireAsync(cancellationToken);
        var client = new AzureDevOpsClient(options.OrganizationUrl, credential);
        try
        {
            var repository = await client.GetRepositoryAsync(options.Project, options.Repository, cancellationToken);
            return _session = new AzureDevOpsSession(client, repository, credential);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed record AzureDevOpsSession(AzureDevOpsClient Client, GitRepository Repository, AzureDevOpsCredential Credential);
}
