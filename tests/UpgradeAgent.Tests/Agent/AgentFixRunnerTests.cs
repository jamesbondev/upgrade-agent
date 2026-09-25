using AgentHarness;
using AgentHarness.Policies;
using AgentHarness.Testing;
using UpgradeAgent.Agent;
using UpgradeAgent.Agent.Activities;
using UpgradeAgent.Config;
using UpgradeAgent.Detection;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;
using UpgradeAgent.Tests.TestSupport;

namespace UpgradeAgent.Tests.Agent;

public sealed class AgentFixRunnerTests : IDisposable
{
    private const string Failed12 = "Build FAILED.\n\n    0 Warning(s)\n    12 Error(s)\n";

    private readonly TempDirectory _worktree = new();
    private readonly TempDirectory _output = new();
    private readonly TempDirectory _packages = new();
    private readonly List<ActivityEvent> _written = [];

    [Fact]
    public async Task FixesThenAsksForTheSummary()
    {
        var backend = new ScriptedBackend("claude-sonnet-4.5")
            .Turn(t => t
                .Usage(2_000, 300, "claude-sonnet-4.5")
                .Shell("dotnet build App.slnx --no-restore", "Build succeeded.\n    0 Error(s)\n")
                .Edit("src/A.cs", "// fixed")
                .Reply("Renamed Format to FormatValue."))
            .Turn(t => t.ReplyJson(new GroupSummary
            {
                Packages = [new PackageSummary { Id = "Fixture.Lib", From = "1.1.0", To = "2.0.0", Status = PackageStatus.Fixed }],
            }));

        var outcome = await FixAsync(backend, new AgentOptions { Model = "claude-sonnet-4.5" });

        Assert.True(outcome.Attempted);
        Assert.StartsWith("finished in ", outcome.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("no structured summary", outcome.Summary, StringComparison.Ordinal);
        Assert.Equal("Fixture.Lib", Assert.Single(outcome.Details!.Packages).Id);
        Assert.Equal(("claude-sonnet-4.5", 1, 2, 2_000L, false), (outcome.Stats!.Model, outcome.Stats.ModelCalls, outcome.Stats.ToolCalls, outcome.Stats.InputTokens, outcome.Stats.BudgetExceeded));
        Assert.Equal("// fixed", _worktree.Read("src/A.cs"));

        Assert.Equal("agent: Scripted (claude-sonnet-4.5) · budget 10 min / 80 tool calls", Assert.IsType<Note>(_written[0]).Text);
        Assert.Contains(new ToolStarted(ToolKind.Edit, "edit", "src/A.cs"), _written);
        Assert.Contains(new BuildChecked(0, ""), _written);
        Assert.Equal(["TASK", "AGENT", "FINAL", "SUMMARY"], _written.OfType<Transcript>().Select(t => t.Label));
        Assert.Contains("repository-relative paths in \"file\"", backend.Messages[1], StringComparison.Ordinal);
        Assert.Contains("no-changes-needed", backend.Messages[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildsWithoutProgressStopTheSessionWithoutASummary()
    {
        var backend = new ScriptedBackend().Turn(t => t
            .Shell("dotnet build App.slnx --no-restore", Failed12)
            .Shell("dotnet build App.slnx --no-restore", Failed12)
            .Reply("Still trying."));

        var outcome = await FixAsync(backend, new AgentOptions { MaxBuildsWithoutProgress = 2 }, initialErrors: 12);

        Assert.StartsWith("agent stopped: 2 builds without reducing the errors below 12 after ", outcome.Summary, StringComparison.Ordinal);
        Assert.True(outcome.Stats!.BudgetExceeded);
        Assert.Null(outcome.Details);
        Assert.Single(backend.Messages);
        Assert.Single(_written, e => e == new Note("agent stopped: 2 builds without reducing the errors below 12"));
        Assert.DoesNotContain(_written, e => e is Transcript { Label: "FINAL" });
    }

    [Fact]
    public async Task RefusalsBeyondTheLimitStopTheSession()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("git commit -am wip").Shell("curl https://example.com").Reply("Done."));

        var outcome = await FixAsync(backend, new AgentOptions { MaxRefusalsPerGroup = 1 });

        Assert.StartsWith("agent stopped: more than 1 refused actions after ", outcome.Summary, StringComparison.Ordinal);
        Assert.Equal(2, outcome.Stats!.Refusals);
        Assert.Contains(_written, e => e is ActionRefused { Action: "git commit -am wip" } refused && refused.Reason.Contains("UpgradeAgent owns commits", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnreadNotesRefuseEditsWithoutCountingTowardTheLimit()
    {
        var notes = Path.Combine(_packages.Path, "fixture.lib", "2.0.0", "MIGRATION.md");
        _packages.Write(Path.Combine("fixture.lib", "2.0.0", "MIGRATION.md"), new string('x', FixPrompts.InlineDocBudget + 1));
        var backend = new ScriptedBackend()
            .Turn(t => t.Edit("src/A.cs").Edit("src/A.cs").Read(notes).Edit("src/A.cs").Reply("Done."))
            .Reply("not json");

        var outcome = await FixAsync(backend, new AgentOptions { MaxRefusalsPerGroup = 1 }, globalPackages: _packages.Path);

        Assert.EndsWith("; no structured summary", outcome.Summary, StringComparison.Ordinal);
        Assert.False(outcome.Stats!.BudgetExceeded);
        Assert.Equal(2, outcome.Stats.Refusals);
        Assert.Equal([false, false, true, true], backend.Decisions.Select(d => d.Allowed));
        Assert.Equal(2, _written.Count(e => e == new ActionRefused("edit src/A.cs", "migration notes not read yet")));
        Assert.StartsWith("Read the migration notes before editing:", backend.Decisions[0].Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderFailuresRejectTheGroupWithoutAbortingTheRun()
    {
        var outcome = await FixAsync(new ScriptedBackend(), new AgentOptions());

        Assert.True(outcome.Attempted);
        Assert.StartsWith("agent failed: The script has no turn left", outcome.Summary, StringComparison.Ordinal);
        Assert.Contains(_written, e => e is Note { Text: var text } && text.StartsWith("agent failed:", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        _worktree.Dispose();
        _output.Dispose();
        _packages.Dispose();
    }

    private async Task<FixOutcome> FixAsync(ScriptedBackend backend, AgentOptions options, int initialErrors = 3, string? globalPackages = null)
    {
        var group = new UpdateGroup("Fixture.Lib", GroupKind.Major, [TestData.Update("Fixture.Lib", "1.1.0", "2.0.0", BumpKind.Major, group: "Fixture.Lib")]);
        var context = new FixContext(
            _worktree.Path, _worktree.Combine("App.slnx"), _output.Path, group, TestData.Build(initialErrors), null, []);
        await using var fixer = new AgentFixRunner(
            backend, options, new PackageDocsLocator(new GlobalPackages(globalPackages)), new AgentActivity(new ListSink(_written)), ApprovalPrompter.DeclineAll,
            TimeProvider.System);
        return await fixer.FixAsync(context, CancellationToken.None);
    }

    private sealed class GlobalPackages(string? folder) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string fileName, IReadOnlyList<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string?>? environment = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(folder is null ? new ProcessResult(1, "", "no dotnet here") : new ProcessResult(0, $"global-packages: {folder}\n", ""));
    }

    private sealed class ListSink(List<ActivityEvent> events) : IActivitySink
    {
        public void Write(ActivityEvent activity) => events.Add(activity);
    }
}
