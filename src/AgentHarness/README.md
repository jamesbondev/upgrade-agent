# AgentHarness

A small .NET 10 library for running GitHub Copilot as an agent in your own app. Copilot owns the tool loop: it
decides which shell commands, file reads and edits, and tool calls to make. The harness decides what each of those
calls may do (run, ask a human, or refuse with feedback the model can act on), bounds the session with time,
tool-call and refusal budgets, and reports every step as a typed event. It was extracted from UpgradeAgent, which
uses the same pattern to fix NuGet upgrade breakages.

- [Quickstart](#quickstart)
- [Concepts](#concepts)
- [Recipes](#recipes)
- [Testing your agent without a model](#testing-your-agent-without-a-model)
- [Security model](#security-model)
- [Troubleshooting](#troubleshooting)
- [File map](#file-map)

## Quickstart

### Prerequisites

- .NET 10 SDK.
- A GitHub Copilot subscription, signed in once on the machine: install the
  [GitHub Copilot CLI](https://docs.github.com/copilot/how-tos/set-up/install-copilot-cli), run `copilot`, then `/login`.
  In a pipeline, use a token instead (see [Running unattended in CI](#running-unattended-in-ci)).
- Network access to `registry.npmjs.org` on the first build. The Copilot SDK downloads its runtime (about 180 MB)
  into your executable project's `obj/` folder. See [Troubleshooting](#the-first-build-downloads-the-copilot-cli)
  for private mirrors.

### Add it to your solution

1. Copy the `AgentHarness` folder into your repo, for example to `src/AgentHarness`.
2. Reference it from your app:

   ```xml
   <ProjectReference Include="../AgentHarness/AgentHarness.csproj" />
   ```

That is all. The csproj builds unchanged with or without central package management. Without it, the csproj pins
`Microsoft.Agents.AI.GitHub.Copilot` itself. With it, the csproj uses your `Directory.Packages.props` entry for
that package if you have one, and otherwise its own version through `VersionOverride`. If you set
`CentralPackageVersionOverrideEnabled` to `false`, add the entry yourself:

```xml
<PackageVersion Include="Microsoft.Agents.AI.GitHub.Copilot" Version="1.22.0" />
```

### Run a session

```csharp
using AgentHarness;
using AgentHarness.Copilot;
using AgentHarness.Policies;

var repo = Path.GetFullPath(args[0]);

await using var copilot = new CopilotBackend();
if (await copilot.CheckAsync(repo) is { Ready: false } status)
{
    Console.Error.WriteLine(status.Message); // what to fix, e.g. how to sign in
    return 1;
}

var runner = new AgentRunner(copilot, ApprovalPrompter.Console);
await using var session = await runner.StartAsync(new AgentSessionOptions
{
    WorkingDirectory = repo,
    Instructions = "You fix failing builds. Check your work with 'dotnet build'.",
    Policy = new WorkspacePolicy(repo, o =>
    {
        o.Commands["dotnet"] = CommandRules.ApproveVerbs("build", "test");
        o.AutoApprovedEditExtensions.Add(".cs");
    }),
    Observers = [new ConsoleAgentObserver(repo)],
});

var reply = await session.SendAsync("Make the build pass.");
Console.WriteLine(reply.Stopped ? $"stopped: {reply.StopReason}" : reply.Text);
Console.WriteLine(session.Stats);
return 0;
```

What this allows: reads inside `repo`, read-only shell commands, `dotnet build` and `dotnet test`, and edits to
`.cs` files run on their own. Edits to other files in `repo` ask you at the console. Everything else is refused,
and the model is told why.

### See it work first

[`samples/HelloAgent`](../../samples/HelloAgent) is a runnable version of the above, plus a custom tool and a
structured summary. Its `--scripted` mode needs no Copilot login and no network:

```sh
dotnet run --project samples/HelloAgent -- --scripted                 # offline, scripted model
dotnet run --project samples/HelloAgent -- ../my-repo "Fix the build"   # real Copilot session
```

## Concepts

```
your app ──► AgentRunner ──► AgentSession ──► IAgentBackend (CopilotBackend) ──► Copilot runtime
                                 │   ▲                                               │
                                 │   └──── every tool call: ToolRequest ◄────────────┘
                                 │         IToolPolicy ─► Approve | Ask ─► IApprovalPrompter | Reject + feedback
                                 └──► AgentEvents ─► IAgentObserver, IStopRule, AgentTelemetry
```

### Backend

`IAgentBackend` is a model provider with an agent runtime. It translates between the provider's SDK and the
harness's neutral types (`ToolRequest`, `AgentEvent`). Everything else lives in the session, so it works the same
for every backend.

`CopilotBackend` is the real one. It starts one Copilot runtime lazily and shares it between sessions. Dispose it
to stop the runtime.

```csharp
await using var copilot = new CopilotBackend(new CopilotOptions
{
    Model = "claude-sonnet-4.5",   // null lets Copilot choose; your plan decides what is served
    ReasoningEffort = "medium",
    HiddenEnvironmentVariables = { "MY_SERVICE_URL" },
    EnvironmentOverrides = { ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1" },
});
```

Every Copilot session is hardened. The working folder cannot change the agent's config, hooks, skills or
instructions (`AGENTS.md`, `.github/copilot-instructions.md` are ignored). Host git operations are off. Sub-agent,
skill and SQL tools are not offered (`CopilotOptions.ExcludedTools`). The runtime's environment, which every
command the agent runs inherits, has no secrets: `AgentEnvironment` drops anything named like a token, key,
password or credential, plus `AZURE_*`, `ARM_*`, `AWS_*` and your `HiddenEnvironmentVariables`.

`CheckAsync` starts the runtime and checks the login. It never throws for a setup problem; `CopilotStatus.Message`
says what to fix. Call it at startup with the folder you will work in, so the runtime starts there once.

### Runner and session

`AgentRunner` starts sessions. One runner can serve many sessions, in sequence or at once. It does not own the
backend.

An `AgentSession` is one conversation. Each `SendAsync` is a turn: the agent works until it replies. The session can
take more turns, and it keeps its context between them.

```csharp
await using var session = await runner.StartAsync(new AgentSessionOptions
{
    Name = "fix-build",                // shows in telemetry
    WorkingDirectory = repo,
    Policy = new WorkspacePolicy(repo),
}, ct);

var first = await session.SendAsync("Make the build pass.", ct);
if (first.Stopped)
{
    Console.WriteLine($"stopped: {first.StopReason}");   // a limit or stop rule; not an exception
    return;
}

var second = await session.SendAsync("Now make the tests pass too.", ct);   // same conversation
```

Two ways a turn ends early:

| What happened | What you get |
|---|---|
| A limit, a stop rule or `session.Stop(reason)` | `AgentReply` with `StopReason` set and `Text` null. Later turns return the same reason at once. |
| Provider failure (not signed in, rate limit, runtime crash) or your own cancellation | An exception from `StartAsync` or `SendAsync`. |

For a one-shot run, `runner.RunAsync(options, message, ct)` starts, sends and disposes, and returns the reply and
`Stats`.

### Policy

`IToolPolicy` decides every tool action. It receives a `ToolRequest`: `ShellRequest`, `FileReadRequest`,
`FileWriteRequest`, `WebFetchRequest`, `CustomToolRequest`, `McpToolRequest` (server, tool, arguments, read-only
flag) or `OtherToolRequest` (memory and anything else the runtime offers). It returns a `ToolDecision`:

| Decision | Effect |
|---|---|
| `ToolDecision.Approve()` | The action runs. |
| `ToolDecision.Ask(reason, prompt, declinedFeedback)` | The prompter asks a human. Declined actions are refused with `declinedFeedback`. |
| `ToolDecision.Reject(feedback)` | Refused. `feedback` goes back to the model, so say what to do instead ("Use the edit tool"), not only what is wrong. |

Refusals count toward `AgentLimits.MaxRefusals` unless you set `CountsTowardRefusalLimit = false` (for nudges such
as "read the docs first"). `LogReason` gives a shorter text for logs than the feedback to the model.

Write a policy with `ToolPolicy.From`:

```csharp
var policy = ToolPolicy.From(request => request switch
{
    FileReadRequest => ToolDecision.Approve(),
    FileWriteRequest { Path: var path } when path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        => ToolDecision.Ask($"edit to {Path.GetFileName(path)}", declinedFeedback: "Leave the project file alone; fix the code instead."),
    _ => ToolDecision.Reject("Only reading and editing files is available. Use the view and edit tools."),
});
```

Or start from `WorkspacePolicy`, the default for a coding agent confined to one folder:

- Reads inside the folder and `ReadOnlyRoots` run.
- Edits run for `AutoApprovedEditExtensions` (with the dot: `".cs"`), ask for other files in the folder, and are
  refused outside it and in `.git`.
- Shell commands are parsed (`ShellCommandParser`). Each part (split on `&&`, `||`, `;`, `|`) must be a
  `ReadOnlyCommands` program, a read-only git command, `cd` inside the folder, or a program with a rule in
  `Commands`. Substitution, variables, redirection, subshells and background jobs are refused.
- Network programs, `rm`/`mv`/`cp`, and unknown programs are refused with feedback.
- Credential files (`SensitiveFileNames`, `SensitiveExtensions`, e.g. `nuget.config`, `.env`, `*.pfx`) are refused
  for every tool.
- Web fetches and approval-required custom tools ask.

```csharp
var workspace = new WorkspacePolicy(repo, o =>
{
    o.Commands["dotnet"] = CommandRules.ApproveVerbs("build", "test");
    o.Commands["npm"] = CommandRules.Ask("npm can run arbitrary scripts");
    o.AutoApprovedEditExtensions.UnionWith([".cs", ".razor"]);
    o.ReadOnlyRoots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"));
    o.SensitiveFileNames.Add("appsettings.Production.json");
});
```

`Commands` rules come first, so they can allow or narrow any program. A `CommandRule` receives the words after the
program name and returns a `ToolDecision`. The refusal texts (`NetworkRefusal`, `GitRefusal`,
`UnknownCommandRefusal`, `SensitiveRefusal`) are settable.

To add one rule on top of a policy, wrap it. The function gets the request and the inner decision:

```csharp
var policy = workspace.Wrap((request, decision) =>
    request is FileWriteRequest write && Path.GetRelativePath(repo, Path.GetFullPath(write.Path, repo)).StartsWith("tests", StringComparison.Ordinal)
        ? ToolDecision.Reject("Don't change the tests; fix the code under src/ so they pass.")
        : decision);
```

Ready-made policies: `ToolPolicy.ApproveAll` (trusted sandboxes and tests only), `ToolPolicy.AskForEverything`,
`ToolPolicy.RejectAll`.

Two checks happen before your policy: web fetches are refused unless `AllowWebFetch` is true, and custom tools
created without `requiresApproval: true` run without asking the policy at all.

### Prompter

`IApprovalPrompter` answers `Ask` decisions. `AgentRunner` uses `ApprovalPrompter.DeclineAll` unless you pass
another, so an unattended run never waits for a human. `ApprovalPrompter.Console` asks `y/N` on the console and
declines when stdin is redirected. The session's time budget is paused while a prompter runs.

```csharp
var prompter = ApprovalPrompter.From(async (action, reason, ct) =>
{
    // Ask in your UI. Return false when nobody can answer, and on cancellation.
    return await myUi.ConfirmAsync($"{action} ({reason})", ct);
});
var runner = new AgentRunner(copilot, prompter);
```

`action` is what the agent wants to do ("edit README.md"), or the decision's `Prompt` when set. `reason` is the
decision's reason.

### Limits and stop rules

`AgentLimits` bounds a session. When a limit is hit, the running turn returns with `StopReason` set.

| Limit | Default | Counts |
|---|---|---|
| `MaxDuration` | 10 minutes | Time the agent works: starting the session and inside `SendAsync`. Time between turns and at approval prompts does not count. |
| `MaxToolCalls` | 80 | `ToolCallStarted` events. Stops on the call after the limit. |
| `MaxRefusals` | 5 | Refusals that count toward the limit. Stops on the one after the limit. |

Set a limit to 0 (or `MaxDuration` to null) to switch it off. `AgentLimits.None` switches all of them off.

For anything else, add a stop rule. It sees every event; returning a reason stops the session.

```csharp
var builds = 0;
var options = new AgentSessionOptions
{
    WorkingDirectory = repo,
    Policy = policy,
    Limits = new AgentLimits { MaxDuration = TimeSpan.FromMinutes(15), MaxToolCalls = 120, MaxRefusals = 3 },
    StopRules =
    [
        AgentObserver.StopWhen(e =>
            e is ToolCallStarted { Kind: ToolKind.Shell, Detail: var command } && command.StartsWith("dotnet build", StringComparison.Ordinal) && ++builds > 6
                ? "agent stopped: more than 6 builds"
                : null),
    ],
};
```

You can also stop a session yourself with `session.Stop(reason)`. The first reason wins.

### Observers and events

Every step of a session is an `AgentEvent`. Observers receive all of them on background threads. Calls are
serialised per session, but an observer shared by concurrent sessions needs its own locking.

| Event | Raised by | Meaning |
|---|---|---|
| `UserMessage(Text)` | session | The app sent a message: the start of a turn. `WithoutTools` marks an `AskAsync` turn, whose reply is data. |
| `AssistantMessage(Text)` | backend | Text the model wrote between tool calls, and its final reply. |
| `ToolCallStarted(CallId, Kind, Tool, Detail, RawArguments)` | backend | A tool call started. `Kind` is `Shell`, `Edit`, `Read` or `Other`; `Detail` is the command, path or pattern. |
| `ToolCallCompleted(CallId, Success, Output, Error)` | backend | A tool call finished. `Call` is the matching `ToolCallStarted`, paired by the session. |
| `ModelUsage(Model, InputTokens, OutputTokens, AiCredits)` | backend | One model call. |
| `ModelServed(Model, Requested)` | session | The first model served, or a change. `IsFallback` is true when it isn't what you asked for. |
| `ToolRefused(Request, Action, Reason, CountsTowardLimit)` | session | A policy refused an action, or the prompter declined it (`DeclinedByOperator`). |
| `ToolApprovedByOperator(Request, Action)` | session | The operator approved an `Ask`. |
| `SessionStopped(Reason)` | session | A limit, stop rule or `Stop` ended the session. |

`ConsoleAgentObserver(workingDirectory, output)` prints one line per event, with paths relative to the working
directory. Write your own for anything richer:

```csharp
var observer = AgentObserver.From(e =>
{
    switch (e)
    {
        case ToolRefused refused:
            Console.WriteLine($"refused {refused.Action}: {refused.Reason}");
            break;
        case ToolCallCompleted { Success: false, Call: { } call } failed:
            Console.WriteLine($"{call.Tool} {call.Detail} failed: {failed.Error}");
            break;
        case ModelServed { IsFallback: true } served:
            Console.WriteLine($"asked for {served.Requested}, got {served.Model}");
            break;
    }
});
```

`session.Stats` (`AgentStats`) totals the session so far: model, model calls, tool calls, tokens, AI credits,
operator approvals, refusals, stop reason and elapsed time.

### Custom tools

An `AgentTool` is one of your functions, offered to the model next to the runtime's shell and file tools. The
parameters and return value are described to the model from the signature and `[Description]` attributes. A
`CancellationToken` parameter is passed through and is not shown to the model.

```csharp
var getTicket = AgentTool.Create(
    ([Description("The ticket key, e.g. ABC-123.")] string key) => tickets.TryGetValue(key, out var text) ? text : $"No ticket {key}.",
    "get_ticket", "Returns a ticket's title and description.");

var deploy = AgentTool.Create(
    async ([Description("The environment: staging or production.")] string environment, CancellationToken ct) => await DeployAsync(environment, ct),
    "deploy", "Deploys the current branch.", requiresApproval: true);

var options = new AgentSessionOptions { WorkingDirectory = repo, Policy = policy, Tools = [getTicket, deploy] };
```

With `requiresApproval: false` (the default) the tool runs whenever the model calls it; the policy is not asked.
Use that only for functions without side effects. With `requiresApproval: true`, each call goes to the policy as a
`CustomToolRequest(Name, Arguments)`; `WorkspacePolicy` asks the operator. Use snake_case names. To wrap an
existing `AIFunction` (from another library or an MCP client), use `AgentTool.From(function, requiresApproval)`.

### Structured replies

`AskAsync<T>` asks a question in a tool-free turn and parses the reply into `T`. The JSON schema in the prompt is
generated from `T`, so the prompt and the parser cannot drift apart. Use `[Description]` to guide the model and
`required` for fields it must fill.

```csharp
internal sealed record Summary
{
    [Description("One sentence: what changed and whether it builds.")]
    public required string Outcome { get; init; }

    [Description("Paths relative to the repo root.")]
    public required IReadOnlyList<string> FilesChanged { get; init; }

    public string? Risks { get; init; }
}
```

```csharp
var reply = await session.SendAsync("Make the build pass.", ct);
if (!reply.Stopped)
{
    var summary = await session.AskAsync<Summary>("Summarise what you changed.", ct);
    Console.WriteLine(summary.Value is { } s ? s.Outcome : $"no summary: {summary.Error}");
}
```

`AskAsync` never throws for a bad or missing reply: `Value` is null and `Error` says why, with the raw reply in
`Text`. Only your own cancellation throws. Every tool is refused during the turn, and it has its own timeout (the
`timeout` parameter, default 2 minutes) outside the session's limits and stop rules. For checks JSON can't express
(non-empty strings, ranges), pass `validate: s => s.Files.Count == 0 ? "no files listed" : null`; a non-null result
becomes the `Error`.

`runner.RunAsync<T>(options, message, question, ct)` does a turn and the question in one call. `Structured` is
null when the turn was stopped. `StructuredOutput.SchemaFor<T>()`, `PromptFor<T>()` and `Parse<T>()` are public if
you need them elsewhere. For a free-text follow-up without tools, use `session.SendWithoutToolsAsync`.

### Telemetry

The harness emits spans and metrics named after the OpenTelemetry GenAI conventions, from an `ActivitySource` and a
`Meter` both called `AgentTelemetry.SourceName` (`"AgentHarness"`). Nothing is exported until you listen:

```csharp
using var tracing = Sdk.CreateTracerProviderBuilder()
    .AddSource(AgentTelemetry.SourceName)
    .AddConsoleExporter()
    .Build();
using var metrics = Sdk.CreateMeterProviderBuilder()
    .AddMeter(AgentTelemetry.SourceName)
    .AddConsoleExporter()
    .Build();
```

- Span `invoke_agent {Name}` per session, ending when the session is disposed. Tags: `gen_ai.provider.name`,
  `gen_ai.request.model`, `gen_ai.response.model`, `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`,
  `agent_harness.session`, `agent_harness.stop_reason`.
- Counter `gen_ai.client.token.usage` (by `gen_ai.response.model`, `gen_ai.token.type`).
- Counter `agent_harness.tool_calls` (by `gen_ai.tool.name`).

Without the OpenTelemetry SDK: `dotnet-counters monitor --counters AgentHarness -n <your app>`.

## Recipes

### Read-only analysis agent

Reuse `WorkspacePolicy`'s checks for reads and shell commands, and refuse everything else:

```csharp
var workspace = new WorkspacePolicy(repo);   // no Commands rules: only read-only programs pass
var readOnly = ToolPolicy.From(request => request switch
{
    FileReadRequest read => workspace.EvaluateRead(read.Path),
    ShellRequest shell => workspace.EvaluateShell(shell.CommandLine, shell.WritesFile, shell.PossiblePaths),
    _ => ToolDecision.Reject("This session is read-only. Read the code and answer; don't change anything."),
});

var result = await new AgentRunner(copilot).RunAsync(new AgentSessionOptions
{
    WorkingDirectory = repo,
    Instructions = "You review code. You never change files.",
    Policy = readOnly,
}, "Where is retry logic implemented, and is it consistent?", ct);
```

For a session that should only talk (classify, summarise), offer no tools at all:

```csharp
var answer = await new AgentRunner(copilot).RunAsync(new AgentSessionOptions
{
    WorkingDirectory = repo,
    Policy = ToolPolicy.RejectAll,
    UseBuiltInTools = false,
}, "Classify this error message: ...", ct);
```

### A session with one approval-required tool

UpgradeAgent publishes this way: a short session whose only tool is `push_branch`, which the operator approves.
The tool itself re-checks what it may push; the model only decides to call it.

```csharp
var push = AgentTool.Create(
    async ([Description("The branch to push.")] string branch, CancellationToken token) => await PushBranchAsync(repo, branch, token),
    "push_branch", "Pushes the verified branch to origin. Call it once.", requiresApproval: true);

var result = await new AgentRunner(copilot, ApprovalPrompter.Console).RunAsync(new AgentSessionOptions
{
    Name = "publish",
    WorkingDirectory = repo,
    Instructions = "Call push_branch once with the branch name you are given, then report the result.",
    Policy = ToolPolicy.From(request => request is CustomToolRequest { Name: "push_branch" }
        ? ToolDecision.Ask("push the branch to origin")
        : ToolDecision.Reject("Only push_branch is available in this session.")),
    Tools = [push],
    UseBuiltInTools = false,
    Limits = new AgentLimits { MaxDuration = TimeSpan.FromMinutes(2), MaxToolCalls = 3, MaxRefusals = 2 },
}, "Publish branch agent/nuget-updates.", ct);
```

### Running unattended in CI

```csharp
var copilot = new CopilotBackend(new CopilotOptions
{
    GitHubTokenEnvironmentVariable = "COPILOT_GITHUB_TOKEN",   // read by the harness, hidden from the agent
});
var runner = new AgentRunner(copilot);   // no prompter: DeclineAll, so every Ask is refused, never a hang
```

- Set the token variable from a pipeline secret. The token's account needs a Copilot seat.
- Keep the default `DeclineAll` prompter. Design the policy so the work can finish without any `Ask`.
- Call `CheckAsync` first and fail the job on `Ready: false`.
- Write the events to a file (next recipe) and publish it as a build artifact.
- Run in a disposable checkout or container. Verify the result with your own build and tests.

### Logging every event to a file

```csharp
await using var log = new StreamWriter(Path.Combine(outDir, "agent.log")) { AutoFlush = true };
await using var session = await runner.StartAsync(new AgentSessionOptions
{
    WorkingDirectory = repo,
    Policy = policy,
    Observers =
    [
        new ConsoleAgentObserver(repo),                                                    // readable, to the console
        AgentObserver.From(e => log.WriteLine($"{DateTimeOffset.Now:HH:mm:ss.fff} {e}")),  // everything, to the file
    ],
}, ct);
```

Events are records, so `ToString()` prints every field. Declare the log before the session so the session is
disposed first. For a short human-readable log, pass the writer to `new ConsoleAgentObserver(repo, log)` instead.

### A provider-specific setting

`CopilotOptions.ConfigureSession` gets the Copilot SDK's `SessionConfig` after the harness has set it up. Use it for
anything the harness does not expose.

```csharp
var copilot = new CopilotBackend(new CopilotOptions
{
    ConfigureSession = config =>
    {
        config.AdditionalDirectories = [docsFolder];   // let the runtime see a folder outside the repo
        config.EnableSessionTelemetry = false;
    },
});
```

This runs last, so it can undo the hardening (`EnableConfigDiscovery`, `EnableSkills`, `OnPermissionRequest`,
`ExcludedTools`). Don't. Calls to MCP servers added through `config.McpServers` reach your policy as
`McpToolRequest(Server, Tool, Arguments, ReadOnly)`; `WorkspacePolicy` asks the operator about them.

### Another backend

Implement `IAgentBackend` and `IAgentBackendSession`. The session does the rest.

```csharp
public sealed class MyProviderBackend : IAgentBackend
{
    public string Name => "My provider";

    public string? Model => "my-model";

    public Task<IAgentBackendSession> StartSessionAsync(AgentBackendSettings settings, CancellationToken cancellationToken) =>
        Task.FromResult<IAgentBackendSession>(new Session(settings));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Session(AgentBackendSettings settings) : IAgentBackendSession
    {
        public async Task<string?> SendAsync(string message, CancellationToken cancellationToken)
        {
            // Your provider's tool loop. Before every tool call the model makes:
            var approval = await settings.AuthorizeAsync(new ShellRequest("dotnet build"), cancellationToken);
            if (!approval.Allowed)
            {
                return approval.Feedback;   // in a real loop: send this back to the model as the tool result
            }

            settings.OnEvent(new ToolCallStarted("1", ToolKind.Shell, "bash", "dotnet build"));
            settings.OnEvent(new ToolCallCompleted("1", true, "Build succeeded.", null));
            settings.OnEvent(new ModelUsage("my-model", 1200, 80));
            settings.OnEvent(new AssistantMessage("Done."));
            return "Done.";
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
```

The contract:

- Call `settings.AuthorizeAsync` before every action, and never act without `Allowed`. Send `Feedback` back to the
  model on a denial.
- Raise `ToolCallStarted`, `ToolCallCompleted`, `AssistantMessage` and `ModelUsage` through `settings.OnEvent`.
- Honour `Tools` (ask with a `CustomToolRequest` before a `RequiresApproval` tool runs), `UseBuiltInTools`,
  `AllowWebFetch` and `Instructions`.
- Honour the `SendAsync` cancellation token promptly. That is how limits and stop rules end a turn.
- Start the agent's processes with `AgentEnvironment.Build(...)` so they get no secrets.

## Testing your agent without a model

`ScriptedBackend` plays back a script of tool calls and replies through the real session: your policy, prompter,
limits, stop rules and observers all run. Shell commands are not executed; they return their scripted output.
Edits with content are written, reads return real files, and `CallTool` runs your real `AgentTool`.
`backend.Decisions` records every permission request and its answer.

```csharp
[Fact]
public async Task Network_is_refused_and_builds_are_allowed()
{
    var repo = Directory.CreateTempSubdirectory().FullName;
    var backend = new ScriptedBackend()
        .Turn(t => t
            .Shell("curl https://example.com")
            .Shell("dotnet build", output: "Build succeeded.")
            .Reply("Done."));

    var result = await new AgentRunner(backend).RunAsync(new AgentSessionOptions
    {
        WorkingDirectory = repo,
        Policy = new WorkspacePolicy(repo, o => o.Commands["dotnet"] = CommandRules.ApproveVerbs("build")),
    }, "Fix the build.");

    Assert.Equal("Done.", result.Reply.Text);
    Assert.Collection(backend.Decisions,
        curl => Assert.False(curl.Allowed),
        build => Assert.True(build.Allowed));
    Assert.Contains("network", backend.Decisions[0].Feedback, StringComparison.OrdinalIgnoreCase);
}

[Fact]
public async Task A_lost_agent_is_stopped()
{
    var repo = Directory.CreateTempSubdirectory().FullName;
    var backend = new ScriptedBackend()
        .Turn(t => t.Shell("rm -rf bin").Shell("wget https://example.com").Reply("never reached"));

    var result = await new AgentRunner(backend).RunAsync(new AgentSessionOptions
    {
        WorkingDirectory = repo,
        Policy = new WorkspacePolicy(repo),
        Limits = new AgentLimits { MaxRefusals = 1 },
    }, "Clean up.");

    Assert.True(result.Reply.Stopped);
    Assert.Equal("agent stopped: more than 1 refused actions", result.Reply.StopReason);
}
```

- One `Turn` per `SendAsync` or `AskAsync`, in order, across all sessions on the backend. A missing turn throws
  `InvalidOperationException`.
- `ReplyJson(value)` answers an `AskAsync<T>`. `Usage(input, output)` adds token counts; without it `Stats` shows
  no model calls.
- `Say`, `Read`, `Edit`, `Fetch` and `Wait` (for time budgets) cover the rest; `Step` runs your own code.
- `Messages` has every message sent, and `LastSettings` what the last session was started with.

For a policy alone you don't need a session: `workspace.EvaluateShell("dotnet build")` returns the decision.

## Security model

The policy is a usability layer on top of a shell that is not sandboxed. It keeps an honest agent on task and
gives it useful feedback. It is not a security boundary against a model that has been manipulated, for example by
instructions hidden in a file it reads.

What the harness does:

- Every tool action goes through your policy first. Nothing runs without an approval.
- Secret-named environment variables are removed from the agent's environment, and the Copilot token variable is
  always hidden.
- Repo-driven configuration is off: the working folder cannot add hooks, skills, custom instructions or config.
- Credential files are refused for every tool, whatever the path.
- Sub-agent, skill and SQL tools are excluded by default. Sub-agents were seen routing around refusals.
- Web fetch is off unless you enable it, and `WorkspacePolicy` then asks for every fetch.
- Budgets stop a session that runs away.

What it does not do:

- Isolate the process. An approved command runs as your user, with your file system and network.
- Understand every program. `WorkspacePolicy` judges commands by name and known flags. It refuses the options it
  knows write files or run programs (`sort -o`, a second file for `uniq`, `tree -o`, `rg --pre`, `sed` scripts other
  than printing and substitution, `git grep -O`, `--ext-diff`, `--textconv`), but a program you add to
  `ReadOnlyCommands` is trusted with every argument. Add only what you understand, and narrow it with a `Commands`
  rule when in doubt.
- Hide secrets that are not in environment variables: files outside the working folder that the agent's commands
  can reach, credential helpers, or a signed-in CLI.
- Check the agent's output. The model's reply and summary are claims, not facts.

So: run agents where a mistake is cheap (a worktree, a container, a CI job), and verify the result independently.
UpgradeAgent rebuilds, reruns the tests and runs deterministic guardrails on the diff after every agent session,
and only then commits.

## Troubleshooting

### "Copilot is not signed in" or "The Copilot runtime didn't start"

That is `CheckAsync`'s message. Sign in once with the Copilot CLI (`copilot`, then `/login`), or set
`CopilotOptions.GitHubTokenEnvironmentVariable` to the name of a variable holding a token. If the runtime didn't
start, the message includes the underlying error; a missing runtime binary means the build did not download it
(next item).

### The first build downloads the Copilot CLI

The Copilot SDK package downloads its runtime from `registry.npmjs.org` into `obj/` of each executable project
that references it. Behind a firewall, set one of these MSBuild properties in your csproj or `Directory.Build.props`:
`CopilotNpmRegistryUrl` (a mirror), `CopilotCliBinaryPath` (a binary you downloaded), or
`CopilotSkipCliDownload=true` (test projects that only use `ScriptedBackend`).

### The model isn't the one I asked for

Your Copilot plan decides which models are served, and the provider falls back silently. Watch for
`ModelServed { IsFallback: true }` (`ConsoleAgentObserver` prints a warning) and read `session.Stats.Model`.

### Warning GHCP001

The Copilot SDK marks its permission API as evaluation-only. The harness suppresses GHCP001 in its own csproj,
so you only see it if your code uses that API directly, for example `PermissionDecision` inside
`ConfigureSession`. Add `<NoWarn>$(NoWarn);GHCP001</NoWarn>` to that project if you do it on purpose.

### The session stops early

Read `reply.StopReason` (also `Stats.StopReason` and the `SessionStopped` event). "more than N refused actions"
means the agent kept trying things the policy refuses: read the `ToolRefused` events, then fix the instructions or
allow what it needs. "more than N tool calls" and "time budget exceeded" mean the task is too big for the limits,
or the agent is looping. After a stop, further `SendAsync` calls return the same reason at once; start a new
session to continue.

### Approval prompts never appear

`ApprovalPrompter.Console` declines without asking when stdin is redirected (pipes, CI, `< /dev/null`, some IDE
run configurations). It prints "approval needed: … — declined (no interactive console)", and the refusal is logged
as "declined (needs operator approval)". Run in a real terminal, or supply your own
`IApprovalPrompter`. Also check that you passed a prompter to `AgentRunner`: the default declines everything.

### `AgentSession` is ambiguous

If you also import `Microsoft.Agents.AI`, it has an `AgentSession` too (CS0104). Alias one of them:

```csharp
using AgentSession = AgentHarness.AgentSession;
```

### An exception from `SendAsync`

Provider failures throw: sign-in expired, rate limits, a crashed runtime. Catch them around the turn and treat
them like any failed step. `AskAsync` does not throw for these; it returns them in `Error`.

## File map

| File | What it holds |
|---|---|
| `AgentHarness.csproj` | Project file. Works with or without central package management. |
| `AgentRunner.cs` | `AgentRunner`, `AgentResult`, `AgentResult<T>`: the front door. |
| `AgentSession.cs` | `AgentSession`: turns, the permission pipeline, limits, event publishing. |
| `AgentSessionOptions.cs` | `AgentSessionOptions`, `AgentLimits`, `IAgentObserver`, `IStopRule`, `AgentObserver`, `AgentReply`, `AgentStats`. |
| `AgentEvents.cs` | `AgentEvent` and every event record; `ToolKind`. |
| `ToolRequests.cs` | `ToolRequest` and its kinds: shell, file read/write, web fetch, custom tool, other. |
| `AgentTool.cs` | `AgentTool`: your functions as tools. |
| `StructuredOutput.cs` | `StructuredOutput`, `StructuredReply<T>`: schema, prompt and lenient parsing. |
| `ConsoleAgentObserver.cs` | One line per event to the console or any `TextWriter`. |
| `IAgentBackend.cs` | `IAgentBackend`, `IAgentBackendSession`, `AgentBackendSettings`, `ToolApproval`: the provider contract. |
| `AgentEnvironment.cs` | Builds the agent's environment without secrets. |
| `Internal.cs` | `AgentTelemetry` (public), plus the pausable time budget and path helpers. |
| `Copilot/CopilotBackend.cs` | `CopilotBackend`, `CopilotStatus`: GitHub Copilot through Agent Framework, hardened. |
| `Copilot/CopilotOptions.cs` | Model, token, environment, excluded tools, `ConfigureSession`. |
| `Copilot/CopilotToolNames.cs` | Built-in Copilot tool names, and the ones excluded by default. |
| `Policies/IToolPolicy.cs` | `IToolPolicy`, `ToolPolicy` (ready-made policies, `From`, `Wrap`). |
| `Policies/ToolDecision.cs` | `ToolDecision`, `ToolVerdict`. |
| `Policies/WorkspacePolicy.cs` | `WorkspacePolicy`, `WorkspacePolicyOptions`, `CommandRule`, `CommandRules`. |
| `Policies/ShellCommandParser.cs` | The small shell tokenizer `WorkspacePolicy` uses. |
| `Policies/ApprovalPrompters.cs` | `IApprovalPrompter`, `ApprovalPrompter`, `ConsoleApprovalPrompter`. |
| `Testing/ScriptedBackend.cs` | `ScriptedBackend`, `ScriptedTurn`, `ScriptedDecision`: a fake model for tests and demos. |
