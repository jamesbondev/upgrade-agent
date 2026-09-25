using AgentHarness.Policies;
using AgentHarness.Testing;
using AgentHarness.Tests.TestSupport;

namespace AgentHarness.Tests;

public sealed class AgentSessionPermissionTests : IDisposable
{
    private readonly TempDirectory _workspace = new();
    private readonly EventRecorder _events = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task AnApprovedActionRuns()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("ls", output: "src").Reply("done"));
        var runner = new AgentRunner(backend);

        var result = await runner.RunAsync(Options(ToolPolicy.ApproveAll), "List the files.");

        var decision = Assert.Single(backend.Decisions);
        Assert.True(decision.Allowed);
        Assert.Equal("done", result.Reply.Text);
    }

    [Fact]
    public async Task ARejectedActionIsRefusedAndTheModelIsToldWhy()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("curl https://example.com").Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(new WorkspacePolicy(_workspace.Path)), "Fetch it.");

        var decision = Assert.Single(backend.Decisions);
        Assert.False(decision.Allowed);
        Assert.Contains("No network access", decision.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedActionIsReportedToObservers()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("curl https://example.com").Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(new WorkspacePolicy(_workspace.Path)), "Fetch it.");

        var refused = Assert.Single(_events.OfType<ToolRefused>());
        Assert.Equal("curl https://example.com", refused.Action);
        Assert.IsType<ShellRequest>(refused.Request);
        Assert.True(refused.CountsTowardLimit);
    }

    [Fact]
    public async Task ARejectedActionDoesNotRun()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("rm -rf src").Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(new WorkspacePolicy(_workspace.Path)), "Clean up.");

        Assert.Empty(_events.OfType<ToolCallStarted>());
    }

    [Fact]
    public async Task AnAskIsDeclinedByDefaultSoUnattendedRunsNeverWait()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("ls").Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(ToolPolicy.AskForEverything), "List the files.");

        var decision = Assert.Single(backend.Decisions);
        Assert.False(decision.Allowed);
        Assert.Contains("operator declined", decision.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAskTheOperatorApprovesRuns()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("ls").Reply("done"));
        var runner = new AgentRunner(backend, ApprovalPrompter.From((_, _) => true));

        await runner.RunAsync(Options(ToolPolicy.AskForEverything), "List the files.");

        Assert.True(Assert.Single(backend.Decisions).Allowed);
        Assert.Single(_events.OfType<ToolCallStarted>());
    }

    [Fact]
    public async Task AnOperatorApprovalIsReportedToObservers()
    {
        var backend = new ScriptedBackend().Turn(t => t.Edit("src/App.csproj").Reply("done"));
        var runner = new AgentRunner(backend, ApprovalPrompter.From((_, _) => true));

        await runner.RunAsync(Options(ToolPolicy.AskForEverything), "Edit the project.");

        var approved = Assert.Single(_events.OfType<ToolApprovedByOperator>());
        Assert.Equal($"edit {Path.Combine("src", "App.csproj")}", approved.Action);
    }

    [Fact]
    public async Task AnAskTheOperatorDeclinesIsRefusedWithTheDefaultFeedback()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("ls").Reply("done"));
        var runner = new AgentRunner(backend, ApprovalPrompter.From((_, _) => false));

        await runner.RunAsync(Options(ToolPolicy.AskForEverything), "List the files.");

        var decision = Assert.Single(backend.Decisions);
        Assert.False(decision.Allowed);
        Assert.Contains("Find another way", decision.Feedback, StringComparison.Ordinal);
        var refused = Assert.Single(_events.OfType<ToolRefused>());
        Assert.Equal("declined (needs operator approval)", refused.Reason);
        Assert.True(refused.DeclinedByOperator);
    }

    [Fact]
    public async Task AnAsksDeclinedFeedbackIsWhatTheModelIsToldWhenTheOperatorDeclines()
    {
        var policy = ToolPolicy.From(_ => ToolDecision.Ask("deploys to production", declinedFeedback: "Don't deploy; report the build result."));
        var backend = new ScriptedBackend().Turn(t => t.Shell("make deploy").Reply("done"));
        var runner = new AgentRunner(backend, ApprovalPrompter.From((_, _) => false));

        await runner.RunAsync(Options(policy), "Ship it.");

        Assert.Equal("Don't deploy; report the build result.", Assert.Single(backend.Decisions).Feedback);
    }

    [Fact]
    public async Task ThePrompterIsShownTheAsksPromptAndReason()
    {
        (string Action, string Reason)? asked = null;
        var policy = ToolPolicy.From(_ => ToolDecision.Ask("deploys to production", prompt: "Deploy main to prod?"));
        var backend = new ScriptedBackend().Turn(t => t.Shell("make deploy").Reply("done"));
        var runner = new AgentRunner(backend, ApprovalPrompter.From((action, reason) =>
        {
            asked = (action, reason);
            return false;
        }));

        await runner.RunAsync(Options(policy), "Ship it.");

        Assert.Equal(("Deploy main to prod?", "deploys to production"), asked);
    }

    [Fact]
    public async Task WithoutAPromptThePrompterIsShownTheActionRelativeToTheWorkingDirectory()
    {
        string? asked = null;
        var backend = new ScriptedBackend().Turn(t => t.Edit("src/App.csproj").Reply("done"));
        var runner = new AgentRunner(backend, ApprovalPrompter.From((action, _) =>
        {
            asked = action;
            return false;
        }));

        await runner.RunAsync(Options(ToolPolicy.AskForEverything), "Edit the project.");

        Assert.Equal($"edit {Path.Combine("src", "App.csproj")}", asked);
    }

    [Fact]
    public async Task AnEditIsWrittenWhenAllowed()
    {
        var policy = new WorkspacePolicy(_workspace.Path, o => o.AutoApprovedEditExtensions.Add(".cs"));
        var backend = new ScriptedBackend().Turn(t => t.Edit("src/A.cs", "class A;").Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(policy), "Add class A.");

        Assert.Equal("class A;", _workspace.Read("src/A.cs"));
    }

    [Fact]
    public async Task AnEditIsNotWrittenWhenRefused()
    {
        var policy = new WorkspacePolicy(_workspace.Path, o => o.AutoApprovedEditExtensions.Add(".cs"));
        var backend = new ScriptedBackend().Turn(t => t.Edit("src/App.csproj", "<Project />").Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(policy), "Edit the project.");

        Assert.False(_workspace.Exists("src/App.csproj"));
    }

    [Fact]
    public async Task WebFetchIsRefusedWhenNotAllowedEvenIfThePolicyApprovesEverything()
    {
        var backend = new ScriptedBackend().Turn(t => t.Fetch("https://example.com").Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(ToolPolicy.ApproveAll, allowWebFetch: false), "Read the docs.");

        var decision = Assert.Single(backend.Decisions);
        Assert.False(decision.Allowed);
        Assert.Contains("Web access is disabled", decision.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebFetchGoesToThePolicyWhenAllowed()
    {
        var backend = new ScriptedBackend().Turn(t => t.Fetch("https://example.com").Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(ToolPolicy.ApproveAll, allowWebFetch: true), "Read the docs.");

        Assert.True(Assert.Single(backend.Decisions).Allowed);
    }

    [Fact]
    public async Task ThePolicyGetsTheRequestAsTheRuntimeSentIt()
    {
        ToolRequest? seen = null;
        var policy = ToolPolicy.From(request =>
        {
            seen = request;
            return ToolDecision.Approve();
        });
        var backend = new ScriptedBackend().Turn(t => t.Edit("src/A.cs").Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(policy), "Edit A.");

        Assert.Equal(new FileWriteRequest(_workspace.Combine("src/A.cs")), seen);
    }

    [Fact]
    public async Task ACustomToolThatNeedsNoApprovalRunsWithoutAsking()
    {
        var calls = 0;
        var tool = AgentTool.Create((string id) => $"ticket {id}: {++calls}", "get_ticket", "Returns a ticket.");
        var backend = new ScriptedBackend().Turn(t => t.CallTool("get_ticket", new { id = "42" }).Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(ToolPolicy.RejectAll, tools: [tool]), "Read ticket 42.");

        Assert.Equal(1, calls);
        Assert.Equal("ticket 42: 1", Assert.Single(_events.OfType<ToolCallCompleted>()).Output);
    }

    [Fact]
    public async Task ASessionAllowsACustomToolThatNeedsNoApprovalWhateverThePolicy()
    {
        var tool = AgentTool.Create(() => "ok", "get_status");
        var backend = new ScriptedBackend().Turn(t => t.Authorize(new CustomToolRequest("get_status")).Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(ToolPolicy.RejectAll, tools: [tool]), "Check the status.");

        Assert.True(Assert.Single(backend.Decisions).Allowed);
    }

    [Fact]
    public async Task ACustomToolThatNeedsApprovalGoesThroughThePolicy()
    {
        var deployed = false;
        var tool = AgentTool.Create(() => deployed = true, "deploy", "Deploys the branch.", requiresApproval: true);
        var backend = new ScriptedBackend().Turn(t => t.CallTool("deploy").Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(ToolPolicy.RejectAll, tools: [tool]), "Ship it.");

        Assert.False(deployed);
        Assert.Equal(new CustomToolRequest("deploy", "{}"), Assert.Single(backend.Decisions).Request);
    }

    [Fact]
    public async Task ACustomToolThatNeedsApprovalRunsOnceTheOperatorApproves()
    {
        var deployed = false;
        var tool = AgentTool.Create(() => deployed = true, "deploy", "Deploys the branch.", requiresApproval: true);
        var backend = new ScriptedBackend().Turn(t => t.CallTool("deploy").Reply("done"));
        var runner = new AgentRunner(backend, ApprovalPrompter.From((_, _) => true));

        await runner.RunAsync(Options(new WorkspacePolicy(_workspace.Path), tools: [tool]), "Ship it.");

        Assert.True(deployed);
    }

    [Fact]
    public async Task AnUnknownCustomToolIsRefused()
    {
        var backend = new ScriptedBackend().Turn(t => t.Authorize(new CustomToolRequest("drop_database")).Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(ToolPolicy.ApproveAll), "Clean up.");

        var decision = Assert.Single(backend.Decisions);
        Assert.False(decision.Allowed);
        Assert.Contains("'drop_database' is not available", decision.Feedback, StringComparison.Ordinal);
        Assert.Single(_events.OfType<ToolRefused>());
    }

    private AgentSessionOptions Options(IToolPolicy policy, bool allowWebFetch = false, IReadOnlyList<AgentTool>? tools = null) => new()
    {
        WorkingDirectory = _workspace.Path,
        Policy = policy,
        AllowWebFetch = allowWebFetch,
        Tools = tools ?? [],
        Limits = AgentLimits.None,
        Observers = [_events],
    };
}
