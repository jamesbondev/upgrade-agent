using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RepoKit.AzureDevOps;

public sealed class AzureDevOpsApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed record AzureDevOpsPullRequest(
    int Id,
    string Title,
    string Status,
    string SourceBranch,
    string TargetBranch,
    bool IsDraft,
    DateTimeOffset Created,
    string WebUrl);

public sealed class AzureDevOpsPullRequests(HttpClient http, AzureDevOpsCredential credential)
{
    public const int MaxDescriptionLength = 4000;
    private const string ApiVersion = "api-version=7.1";
    private const int PageSize = 100;
    private const int MaxPages = 20;

    public async Task<IReadOnlyList<AzureDevOpsPullRequest>> ListAsync(AzureDevOpsRepo repo, string sourceBranchPrefix, CancellationToken cancellationToken)
    {
        var found = new List<AzureDevOpsPullRequest>();
        for (var page = 0; page < MaxPages; page++)
        {
            using var response = await SendAsync(
                HttpMethod.Get, $"{RepoApi(repo)}/pullrequests?searchCriteria.status=all&$top={PageSize}&$skip={page * PageSize}&{ApiVersion}", null, cancellationToken);
            using var document = await ReadAsync(response, cancellationToken);
            var values = document.RootElement.GetProperty("value").EnumerateArray().ToList();
            found.AddRange(values
                .Select(v => Parse(v, repo))
                .Where(pr => pr.SourceBranch.StartsWith(sourceBranchPrefix, StringComparison.Ordinal)));
            if (values.Count < PageSize)
            {
                break;
            }
        }

        return found;
    }

    public async Task<AzureDevOpsPullRequest> CreateDraftAsync(
        AzureDevOpsRepo repo, string sourceBranch, string targetBranch, string title, string description, IReadOnlyList<string> labels, CancellationToken cancellationToken)
    {
        var fitted = Fit(description, MaxDescriptionLength);
        var body = new
        {
            sourceRefName = $"refs/heads/{sourceBranch}",
            targetRefName = $"refs/heads/{targetBranch}",
            title,
            description = fitted,
            isDraft = true,
            labels = labels.Select(l => new { name = l }).ToList(),
        };

        using var response = await SendAsync(HttpMethod.Post, $"{RepoApi(repo)}/pullrequests?{ApiVersion}", JsonContent.Create(body), cancellationToken);
        using var document = await ReadAsync(response, cancellationToken);
        var pullRequest = Parse(document.RootElement, repo);

        if (fitted.Length < description.Length)
        {
            var thread = new { comments = new[] { new { parentCommentId = 0, content = description, commentType = 1 } }, status = "closed" };
            using var threadResponse = await SendAsync(
                HttpMethod.Post, $"{RepoApi(repo)}/pullRequests/{pullRequest.Id}/threads?{ApiVersion}", JsonContent.Create(thread), cancellationToken);
            (await ReadAsync(threadResponse, cancellationToken)).Dispose();
        }

        return pullRequest;
    }

    public static string Fit(string markdown, int limit)
    {
        if (markdown.Length <= limit)
        {
            return markdown;
        }

        var notice = string.Create(CultureInfo.InvariantCulture, $"\n\n_Truncated to fit the {limit:N0}-character limit. The full description is the first comment._");
        var cut = markdown[..(limit - notice.Length)];
        var lastLine = cut.LastIndexOf('\n');
        return (lastLine > 0 ? cut[..lastLine] : cut) + notice;
    }

    private static string RepoApi(AzureDevOpsRepo repo) =>
        $"{repo.OrganizationUrl}/{Uri.EscapeDataString(repo.Project)}/_apis/git/repositories/{Uri.EscapeDataString(repo.Repository)}";

    private static AzureDevOpsPullRequest Parse(JsonElement value, AzureDevOpsRepo repo)
    {
        var id = value.GetProperty("pullRequestId").GetInt32();
        return new AzureDevOpsPullRequest(
            id,
            value.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
            value.TryGetProperty("status", out var status) ? status.GetString() ?? "" : "",
            BranchName(value, "sourceRefName"),
            BranchName(value, "targetRefName"),
            value.TryGetProperty("isDraft", out var draft) && draft.ValueKind == JsonValueKind.True,
            value.TryGetProperty("creationDate", out var created) ? created.GetDateTimeOffset() : DateTimeOffset.MinValue,
            $"{repo.WebUrl}/pullrequest/{id}");
    }

    private static string BranchName(JsonElement value, string property) =>
        value.TryGetProperty(property, out var reference) && reference.GetString() is { } name
            ? name.StartsWith("refs/heads/", StringComparison.Ordinal) ? name["refs/heads/".Length..] : name
            : "";

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        var header = credential.AuthorizationHeader.Split(' ', 2);
        request.Headers.Authorization = new AuthenticationHeaderValue(header[0], header.Length > 1 ? header[1] : null);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await http.SendAsync(request, cancellationToken);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var isJson = response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
        if (response.IsSuccessStatusCode && isJson)
        {
            return JsonDocument.Parse(text);
        }

        var status = (int)response.StatusCode;
        if (!isJson)
        {
            throw new AzureDevOpsApiException(status, $"Azure DevOps didn't return JSON (HTTP {status}); the credential is probably not valid for this organization.");
        }

        string? message = null;
        try
        {
            using var error = JsonDocument.Parse(text);
            message = error.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
        }
        catch (JsonException)
        {
        }

        throw new AzureDevOpsApiException(status, $"Azure DevOps returned HTTP {status}: {message ?? text}");
    }
}
