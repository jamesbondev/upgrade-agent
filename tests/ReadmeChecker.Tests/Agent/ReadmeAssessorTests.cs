using AgentHarness;
using AgentHarness.Testing;
using ReadmeChecker.Agent;
using ReadmeChecker.Config;
using ReadmeChecker.Detection;
using ReadmeChecker.Tests.TestSupport;
using RepoKit;

namespace ReadmeChecker.Tests.Agent;

public sealed class ReadmeAssessorTests : IDisposable
{
    private readonly TempDirectory _repo = new TempDirectory().Write("README.md", "Run `dotnet run --project src/Old`.").Write("src/New/New.csproj", "<Project />");
    private readonly TempDirectory _out = new();
    private readonly RepoFacts _facts = FactsBuilder.Readme("Run `dotnet run --project src/Old`.", ["src/New/New.csproj"]);

    public void Dispose()
    {
        _repo.Dispose();
        _out.Dispose();
    }

    [Fact]
    public async Task TheAgentExploresThenAnswersAndItsIssuesAreValidated()
    {
        var backend = new ScriptedBackend()
            .Turn(t => t.Read("README.md").Shell("ls src", output: "New").Reply("src/Old is gone."))
            .Turn(t => t.ReplyJson(new ReadmeAssessment
            {
                Verdict = AssessedVerdict.Stale,
                Summary = "The run command points at a removed project.",
                Issues =
                [
                    new ReadmeIssue { Kind = IssueKind.WrongCommand, Quote = "dotnet run --project src/Old", Evidence = ["src/New/New.csproj"], SuggestedFix = "Use src/New." },
                    new ReadmeIssue { Kind = IssueKind.Other, Quote = "made up", SuggestedFix = "?" },
                ],
            }));
        var factory = new ScriptedFactory(backend);

        var outcome = await Assessor(factory).AssessAsync("demo", _repo.Path, _facts, SignalScan.Empty, _out.Combine("demo/agent.log"), null, CancellationToken.None);

        Assert.Null(outcome.Failure);
        Assert.Equal(AssessedVerdict.Stale, outcome.Verdict);
        Assert.Equal("dotnet run --project src/Old", Assert.Single(outcome.Issues).Quote);
        Assert.Equal("made up", Assert.Single(outcome.Rejected).Issue.Quote);
        Assert.Equal(1, factory.ReadyChecks);
        Assert.Contains("<readme>", backend.Messages[0], StringComparison.Ordinal);
        Assert.Contains("ls src", _out.Read("demo/agent.log"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSessionIsReadOnly()
    {
        var backend = new ScriptedBackend()
            .Turn(t => t.Edit("README.md", "changed").Shell("rm -rf src").Reply("done"))
            .Turn(t => t.ReplyJson(new ReadmeAssessment { Verdict = AssessedVerdict.Current, Summary = "fine" }));

        await Assessor(new ScriptedFactory(backend)).AssessAsync("demo", _repo.Path, _facts, SignalScan.Empty, _out.Combine("log"), null, CancellationToken.None);

        Assert.Collection(
            backend.Decisions,
            edit => Assert.Equal((false, ReadmeAssessor.ReadOnlyRefusal), (edit.Allowed, edit.Feedback)),
            remove => Assert.False(remove.Allowed));
        Assert.Equal("Run `dotnet run --project src/Old`.", _repo.Read("README.md"));
    }

    [Fact]
    public async Task AStoppedSessionIsAFailure()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("ls").Shell("ls src").Shell("ls docs").Reply("never"));
        var options = new AgentOptions { MaxToolCalls = 2 };

        var outcome = await Assessor(new ScriptedFactory(backend), options).AssessAsync("demo", _repo.Path, _facts, SignalScan.Empty, _out.Combine("log"), null, CancellationToken.None);

        Assert.StartsWith("the agent was stopped", outcome.Failure, StringComparison.Ordinal);
        Assert.NotNull(outcome.Stats);
    }

    [Fact]
    public async Task AnAnswerThatIsntJsonIsAFailure()
    {
        var backend = new ScriptedBackend().Reply("checked").Reply("It looks fine to me.");

        var outcome = await Assessor(new ScriptedFactory(backend)).AssessAsync("demo", _repo.Path, _facts, SignalScan.Empty, _out.Combine("log"), null, CancellationToken.None);

        Assert.StartsWith("the agent's answer wasn't usable", outcome.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProviderFailureIsAFailureNotAnException()
    {
        var outcome = await Assessor(new ScriptedFactory(new ScriptedBackend())).AssessAsync("demo", _repo.Path, _facts, SignalScan.Empty, _out.Combine("log"), null, CancellationToken.None);

        Assert.StartsWith("the agent failed", outcome.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopilotNotBeingReadyStopsTheRun()
    {
        var factory = new ScriptedFactory(new ScriptedBackend()) { NotReady = "Copilot is not signed in." };

        var error = await Assert.ThrowsAsync<AgentUnavailableException>(() =>
            Assessor(factory).AssessAsync("demo", _repo.Path, _facts, SignalScan.Empty, _out.Combine("log"), null, CancellationToken.None));

        Assert.Equal("Copilot is not signed in.", error.Message);
    }

    [Fact]
    public void CopilotOptionsHideTheCredentialVariables()
    {
        var options = CopilotBackendFactory.CopilotOptionsFor(new AgentOptions { Model = "m", RemoveEnvironmentVariables = ["MY_SECRET"] }, ["ADO_PAT", "SYSTEM_ACCESSTOKEN"]);

        Assert.Equal(["MY_SECRET", "ADO_PAT", "SYSTEM_ACCESSTOKEN"], options.HiddenEnvironmentVariables);
        Assert.Equal(("m", "ReadmeChecker"), (options.Model, options.ClientName));
    }

    private static ReadmeAssessor Assessor(IAgentBackendFactory factory, AgentOptions? options = null) =>
        new(factory, options ?? new AgentOptions(), new GitCli(new ProcessRunner()), TimeProvider.System);

    internal sealed class ScriptedFactory(ScriptedBackend backend) : IAgentBackendFactory
    {
        public int ReadyChecks { get; private set; }

        public string? NotReady { get; init; }

        public IAgentBackend Create() => backend;

        public Task EnsureReadyAsync(IAgentBackend agentBackend, string workingDirectory, CancellationToken cancellationToken)
        {
            ReadyChecks++;
            return NotReady is null ? Task.CompletedTask : throw new AgentUnavailableException(NotReady);
        }
    }
}
