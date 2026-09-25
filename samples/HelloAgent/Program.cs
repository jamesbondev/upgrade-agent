using System.ComponentModel;
using AgentHarness;
using AgentHarness.Copilot;
using AgentHarness.Policies;
using HelloAgent;

// dotnet run -- <folder> "<task>"   a real Copilot session in <folder>
// dotnet run -- --scripted          the same flow, offline, with ScriptedBackend
var scripted = args is ["--scripted"];
if (!scripted && (args.Length != 2 || !Directory.Exists(args[0])))
{
    Console.Error.WriteLine("usage: HelloAgent <folder> \"<task>\"  |  HelloAgent --scripted");
    return 2;
}

var folder = scripted ? ScriptedDemo.CreateWorkspace() : Path.GetFullPath(args[0]);
var task = scripted ? "Resolve the TODO in the greeter." : args[1];
await using IAgentBackend backend = scripted ? ScriptedDemo.Backend() : new CopilotBackend();

// Fail fast: a missing login is a friendly message now, not an exception mid-session.
if (backend is CopilotBackend copilot && await copilot.CheckAsync(folder) is { Ready: false } status)
{
    Console.Error.WriteLine(status.Message);
    return 1;
}

// Our own tool. No side effects, so it runs without asking (requiresApproval: false).
var listTodos = AgentTool.Create(() => ListTodos(folder), "list_todos", "Lists the TODO comments in the workspace's C# files.");

// Actions the policy answers with Ask go to this prompter. Redirected stdin means nobody can answer: it declines.
var runner = new AgentRunner(backend, ApprovalPrompter.Console);
await using var session = await runner.StartAsync(new AgentSessionOptions
{
    Name = "hello-agent",
    WorkingDirectory = folder,
    Instructions = "You make small, careful C# changes. Check your work with 'dotnet build'.",
    Policy = new WorkspacePolicy(folder, o =>
    {
        o.Commands["dotnet"] = CommandRules.ApproveVerbs("build", "test"); // other dotnet verbs are refused
        o.AutoApprovedEditExtensions.Add(".cs");                             // other edits ask the operator
    }),
    Tools = [listTodos],
    Limits = new AgentLimits { MaxDuration = TimeSpan.FromMinutes(5), MaxToolCalls = 40, MaxRefusals = 5 },
    Observers = [new ConsoleAgentObserver(folder)],
});

Console.WriteLine($"> {task}");
var reply = await session.SendAsync(task);
if (reply.Stopped)
{
    // A limit or stop rule ended the turn. Provider failures throw instead.
    Console.WriteLine($"stopped: {reply.StopReason}");
}
else
{
    // A second, tool-free turn. The JSON schema comes from Summary; a bad reply is an Error, not an exception.
    var summary = await session.AskAsync<Summary>("Summarise what you changed.");
    Console.WriteLine(summary.Value is { } s
        ? $"summary: {s.Outcome} [{string.Join(", ", s.FilesChanged)}]"
        : $"no summary: {summary.Error}");
}

Console.WriteLine(session.Stats); // time, calls, tokens, refusals, approvals, stop reason
return 0;

static string ListTodos(string folder)
{
    var todos = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
        .SelectMany(file => File.ReadLines(file)
            .Where(line => line.Contains("TODO", StringComparison.Ordinal))
            .Select(line => $"{Path.GetRelativePath(folder, file)}: {line.Trim()}"))
        .ToList();
    return todos.Count == 0 ? "No TODOs." : string.Join('\n', todos);
}

namespace HelloAgent
{
    internal sealed record Summary
    {
        [Description("One sentence: what changed and whether the build passes.")]
        public required string Outcome { get; init; }

        [Description("Paths of the files you changed, relative to the workspace.")]
        public required IReadOnlyList<string> FilesChanged { get; init; }
    }
}
