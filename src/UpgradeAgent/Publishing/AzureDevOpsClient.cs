using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.VisualStudio.Services.WebApi;

namespace UpgradeAgent.Publishing;

/// <summary>The few Azure DevOps operations a run needs, over the official .NET client (VssConnection + GitHttpClient).</summary>
internal sealed class AzureDevOpsClient(string organizationUrl, AzureDevOpsCredential credential) : IDisposable
{
    public const string BranchPrefix = "agent/nuget-updates-";

    /// <summary>Azure DevOps rejects PR descriptions longer than this.</summary>
    public const int MaxDescriptionLength = 4000;

    private readonly VssConnection _connection = new(new Uri(organizationUrl), credential.VssCredentials);

    public async Task<GitRepository> GetRepositoryAsync(string project, string repository, CancellationToken cancellationToken)
    {
        var git = await _connection.GetClientAsync<GitHttpClient>(cancellationToken);
        return await git.GetRepositoryAsync(project, repository, cancellationToken: cancellationToken);
    }

    /// <summary>Creates a draft PR with the label. If the description is too long, the full text goes in the first comment.</summary>
    public async Task<GitPullRequest> CreateDraftPullRequestAsync(
        GitRepository repository, string branch, string title, string description, string label, CancellationToken cancellationToken)
    {
        var git = await _connection.GetClientAsync<GitHttpClient>(cancellationToken);
        var fitted = FitDescription(description);
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

    /// <summary>Active PRs from agent branches that carry the label: what a demo reset abandons.</summary>
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

    /// <summary>Drops collapsible detail sections first, then truncates at a line boundary with a pointer to the full text.</summary>
    public static string FitDescription(string markdown, int limit = MaxDescriptionLength)
    {
        if (markdown.Length <= limit)
        {
            return markdown;
        }

        var withoutDetails = System.Text.RegularExpressions.Regex.Replace(markdown, @"<details>.*?</details>\s*", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        if (withoutDetails.Length <= limit)
        {
            return withoutDetails;
        }

        const string Notice = "\n\n_Truncated to fit Azure DevOps' 4,000-character limit. The full report is the first comment._";
        var cut = withoutDetails[..(limit - Notice.Length)];
        var lastLine = cut.LastIndexOf('\n');
        return (lastLine > 0 ? cut[..lastLine] : cut) + Notice;
    }

    public void Dispose() => _connection.Dispose();
}
