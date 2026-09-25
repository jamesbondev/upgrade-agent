using System.Net;
using System.Text;
using System.Text.Json;

namespace RepoKit.AzureDevOps.Tests;

public sealed class AzureDevOpsPullRequestsTests : IDisposable
{
    private static readonly AzureDevOpsRepo Repo = new("https://dev.azure.com/contoso", "Team Project", "api");
    private readonly FakeHandler _handler = new();
    private readonly HttpClient _http;

    public AzureDevOpsPullRequestsTests() => _http = new HttpClient(_handler);

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }

    [Fact]
    public async Task ListFindsPullRequestsInAnyStatusByBranchPrefix()
    {
        _handler.Respond(HttpStatusCode.OK, """
            { "value": [
              { "pullRequestId": 7, "title": "Update README", "status": "completed", "sourceRefName": "refs/heads/agent/readme-refresh-20260901-1200",
                "targetRefName": "refs/heads/main", "isDraft": false, "creationDate": "2026-09-01T12:00:00Z" },
              { "pullRequestId": 8, "title": "Other", "status": "active", "sourceRefName": "refs/heads/feature/x", "targetRefName": "refs/heads/main" }
            ] }
            """);

        var found = await Client().ListAsync(Repo, "agent/readme-refresh-", CancellationToken.None);

        var pullRequest = Assert.Single(found);
        Assert.Equal((7, "completed", "agent/readme-refresh-20260901-1200", "main"), (pullRequest.Id, pullRequest.Status, pullRequest.SourceBranch, pullRequest.TargetBranch));
        Assert.Equal("https://dev.azure.com/contoso/Team%20Project/_git/api/pullrequest/7", pullRequest.WebUrl);
        var request = Assert.Single(_handler.Requests);
        Assert.StartsWith("https://dev.azure.com/contoso/Team%20Project/_apis/git/repositories/api/pullrequests?searchCriteria.status=all", request.Url, StringComparison.Ordinal);
        Assert.Equal("Basic c2VjcmV0", request.Authorization);
    }

    [Fact]
    public async Task ListReadsEveryPage()
    {
        var page = JsonSerializer.Serialize(new { value = Enumerable.Range(1, 100).Select(i => new { pullRequestId = i, sourceRefName = "refs/heads/agent/readme-refresh-x" }) });
        _handler.Respond(HttpStatusCode.OK, page);
        _handler.Respond(HttpStatusCode.OK, """{ "value": [ { "pullRequestId": 101, "sourceRefName": "refs/heads/agent/readme-refresh-y" } ] }""");

        var found = await Client().ListAsync(Repo, "agent/readme-refresh-", CancellationToken.None);

        Assert.Equal(101, found.Count);
        Assert.Contains("$skip=100", _handler.Requests[1].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateDraftSendsADraftWithLabels()
    {
        _handler.Respond(HttpStatusCode.Created, """{ "pullRequestId": 12, "title": "Update README", "status": "active", "isDraft": true, "sourceRefName": "refs/heads/agent/readme-refresh-1", "targetRefName": "refs/heads/main" }""");

        var pullRequest = await Client().CreateDraftAsync(Repo, "agent/readme-refresh-1", "main", "Update README", "Body", ["agent-generated"], CancellationToken.None);

        Assert.Equal((12, true), (pullRequest.Id, pullRequest.IsDraft));
        using var body = JsonDocument.Parse(Assert.Single(_handler.Requests).Body!);
        var root = body.RootElement;
        Assert.Equal("refs/heads/agent/readme-refresh-1", root.GetProperty("sourceRefName").GetString());
        Assert.Equal("refs/heads/main", root.GetProperty("targetRefName").GetString());
        Assert.True(root.GetProperty("isDraft").GetBoolean());
        Assert.Equal("agent-generated", root.GetProperty("labels")[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ALongDescriptionIsTrimmedAndPostedInFullAsAComment()
    {
        _handler.Respond(HttpStatusCode.Created, """{ "pullRequestId": 12 }""");
        _handler.Respond(HttpStatusCode.OK, """{ "id": 1 }""");
        var description = string.Join('\n', Enumerable.Range(0, 400).Select(i => $"line {i} with some text"));

        await Client().CreateDraftAsync(Repo, "b", "main", "t", description, [], CancellationToken.None);

        using var created = JsonDocument.Parse(_handler.Requests[0].Body!);
        Assert.True(created.RootElement.GetProperty("description").GetString()!.Length <= AzureDevOpsPullRequests.MaxDescriptionLength);
        Assert.EndsWith("/pullRequests/12/threads?api-version=7.1", _handler.Requests[1].Url, StringComparison.Ordinal);
        Assert.Contains("line 399", _handler.Requests[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnErrorCarriesAzureDevOpsMessage()
    {
        _handler.Respond(HttpStatusCode.Conflict, """{ "message": "An active pull request for the source and target branch already exists." }""");

        var error = await Assert.ThrowsAsync<AzureDevOpsApiException>(() => Client().CreateDraftAsync(Repo, "b", "main", "t", "d", [], CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASignInPageMeansTheCredentialIsntValid()
    {
        _handler.Respond(HttpStatusCode.NonAuthoritativeInformation, "<html>Sign in</html>", "text/html");

        var error = await Assert.ThrowsAsync<AzureDevOpsApiException>(() => Client().ListAsync(Repo, "x", CancellationToken.None));

        Assert.Contains("credential", error.Message, StringComparison.Ordinal);
    }

    private AzureDevOpsPullRequests Client() => new(_http, new AzureDevOpsCredential("test", "Basic c2VjcmV0"));

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body, string MediaType)> _responses = new();

        public List<(string Url, string? Authorization, string? Body)> Requests { get; } = [];

        public void Respond(HttpStatusCode status, string body, string mediaType = "application/json") => _responses.Enqueue((status, body, mediaType));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), body));
            var (status, text, mediaType) = _responses.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, mediaType) };
        }
    }
}
