using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace UpgradeAgent.Publishing;

internal sealed class AzureDevOpsClient(string organizationUrl, AzureDevOpsCredential credential) : IDisposable
{
    public const string BranchPrefix = "agent/nuget-updates-";

    public const int MaxDescriptionLength = 4000;

    private readonly VssConnection _connection = new(new Uri(organizationUrl), credential.VssCredentials);

    public async Task<GitRepository> GetRepositoryAsync(string project, string repository, CancellationToken cancellationToken)
    {
        var git = await _connection.GetClientAsync<GitHttpClient>(cancellationToken);
        return await git.GetRepositoryAsync(project, repository, cancellationToken: cancellationToken);
    }

    public async Task<GitPullRequest> CreateDraftPullRequestAsync(
        GitRepository repository, string branch, string title, string description, string label, CancellationToken cancellationToken)
    {
        var git = await _connection.GetClientAsync<GitHttpClient>(cancellationToken);
        var fitted = PrDescription.Fit(description, MaxDescriptionLength);
        var pullRequest = await git.CreatePullRequestAsync(
            new GitPullRequest
            {
                SourceRefName = $"refs/heads/{branch}",
                TargetRefName = repository.DefaultBranch,
                Title = title,
                Description = fitted,
                IsDraft = true,
                Labels = [new Microsoft.TeamFoundation.Core.WebApi.WebApiTagDefinition { Name = label }],
            },
            repository.Id,
            cancellationToken: cancellationToken);

        if (fitted.Length < description.Length)
        {
            await git.CreateThreadAsync(
                new GitPullRequestCommentThread { Comments = [new Comment { Content = description }], Status = CommentThreadStatus.Closed },
                repository.Id,
                pullRequest.PullRequestId,
                cancellationToken: cancellationToken);
        }

        return pullRequest;
    }

    public static string WebUrl(GitRepository repository, GitPullRequest pullRequest) =>
        $"{repository.WebUrl}/pullrequest/{pullRequest.PullRequestId}";

    public async Task<IReadOnlyList<GitPullRequest>> FindAgentPullRequestsAsync(GitRepository repository, string label, CancellationToken cancellationToken)
    {
        var git = await _connection.GetClientAsync<GitHttpClient>(cancellationToken);
        var active = await git.GetPullRequestsAsync(repository.Id, new GitPullRequestSearchCriteria { Status = PullRequestStatus.Active }, cancellationToken: cancellationToken);
        return active
            .Where(pr => pr.SourceRefName.StartsWith($"refs/heads/{BranchPrefix}", StringComparison.Ordinal))
            .Where(pr => pr.Labels?.Any(l => string.Equals(l.Name, label, StringComparison.OrdinalIgnoreCase)) == true)
            .ToList();
    }

    public async Task<IReadOnlyList<GitRef>> FindAgentBranchesAsync(GitRepository repository, CancellationToken cancellationToken)
    {
        var git = await _connection.GetClientAsync<GitHttpClient>(cancellationToken);
        return await git.GetRefsAsync(repository.Id, filter: $"heads/{BranchPrefix}", cancellationToken: cancellationToken);
    }

    public async Task AbandonAsync(GitRepository repository, GitPullRequest pullRequest, CancellationToken cancellationToken)
    {
        var git = await _connection.GetClientAsync<GitHttpClient>(cancellationToken);
        await git.UpdatePullRequestAsync(new GitPullRequest { Status = PullRequestStatus.Abandoned }, repository.Id, pullRequest.PullRequestId, cancellationToken: cancellationToken);
    }

    public async Task DeleteBranchAsync(GitRepository repository, GitRef branch, CancellationToken cancellationToken)
    {
        var git = await _connection.GetClientAsync<GitHttpClient>(cancellationToken);
        var results = await git.UpdateRefsAsync(
            [new GitRefUpdate { Name = branch.Name, OldObjectId = branch.ObjectId, NewObjectId = new string('0', 40) }],
            repository.Id,
            cancellationToken: cancellationToken);
        if (results.FirstOrDefault() is { Success: false } failure)
        {
            throw new InvalidOperationException($"Could not delete {branch.Name}: {failure.UpdateStatus}");
        }
    }

    public void Dispose() => _connection.Dispose();
}
