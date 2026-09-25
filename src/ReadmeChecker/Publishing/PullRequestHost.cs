using ReadmeChecker.Config;
using RepoKit.AzureDevOps;

namespace ReadmeChecker.Publishing;

internal interface IPullRequestHost
{
    Task<IReadOnlyList<AzureDevOpsPullRequest>> ListAsync(string branchPrefix, CancellationToken cancellationToken);

    Task<AzureDevOpsPullRequest> CreateDraftAsync(string branch, string targetBranch, string title, string description, CancellationToken cancellationToken);
}

internal interface IPullRequestHosts
{
    IPullRequestHost? For(RepoTarget target, AzureDevOpsCredential? credential);
}

internal sealed class AzureDevOpsPullRequestHosts(HttpClient http, PublishOptions options) : IPullRequestHosts
{
    public IPullRequestHost? For(RepoTarget target, AzureDevOpsCredential? credential) =>
        target.AzureDevOps is { } repo && credential is not null
            ? new Host(new AzureDevOpsPullRequests(http, credential), repo, options.Label)
            : null;

    private sealed class Host(AzureDevOpsPullRequests pullRequests, AzureDevOpsRepo repo, string label) : IPullRequestHost
    {
        public Task<IReadOnlyList<AzureDevOpsPullRequest>> ListAsync(string branchPrefix, CancellationToken cancellationToken) =>
            pullRequests.ListAsync(repo, branchPrefix, cancellationToken);

        public Task<AzureDevOpsPullRequest> CreateDraftAsync(string branch, string targetBranch, string title, string description, CancellationToken cancellationToken) =>
            pullRequests.CreateDraftAsync(repo, branch, targetBranch, title, description, [label], cancellationToken);
    }
}
