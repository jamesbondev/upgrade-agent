# AgentHarness: a 15-minute tour

Presenter's outline. Each heading is one slide. "Show" is what goes on screen.

## 1. The problem (1 min)

- Copilot can run a real agent loop from .NET: shell, file edits, our own functions.
- Out of the box it will run whatever it decides to run, for as long as it likes, and we see little of it.
- UpgradeAgent needed three things around that loop: rules per tool call, budgets, and a full record.
- AgentHarness is those three things, extracted into one folder you can copy.

Show: the diagram in `src/AgentHarness/README.md` (Concepts).

## 2. The whole API on one slide (1 min)

- `CopilotBackend`: talks to Copilot. `AgentRunner`: starts sessions. `AgentSession`: one conversation.
- `IToolPolicy`: decides each tool call. `IApprovalPrompter`: asks a human when the policy says Ask.
- `AgentLimits` and stop rules: end a runaway session. Observers: see every event.
- `AgentTool`: our functions as tools. `AskAsync<T>`: a typed summary at the end.

Show: `samples/HelloAgent/Program.cs`, top to bottom.

## 3. Fail fast before any work (1 min)

- `CheckAsync` starts the runtime and checks the login; it returns a message, never throws for setup problems.
- The message says what to do: `copilot`, then `/login`, or a token variable in CI.
- Pass the same folder you will work in, so the runtime starts once.

Show: `Program.cs`, the `CheckAsync` block. `Copilot/CopilotBackend.cs`, `CheckAsync`.

## 4. Policy: Approve, Ask, Reject (2 min)

- Every shell command, read, edit, fetch and approval-required tool call becomes a `ToolRequest`.
- The policy answers `Approve`, `Ask` (a human decides) or `Reject` with feedback the model can act on.
- Good feedback says what to do instead: "Use the edit tool", not "denied".
- `WorkspacePolicy` is the default: reads and read-only commands run; `Commands["dotnet"]` adds a program;
  `AutoApprovedEditExtensions` decides which edits run without asking.

Show: `Policies/WorkspacePolicy.cs`, the class summary and `EvaluateSegment`. `Program.cs`, the policy lambda.

## 5. Adding a rule without rewriting the policy (1 min)

- `ToolPolicy.From(request => ...)` for a whole policy in a switch expression.
- `policy.Wrap((request, decision) => ...)` to add one rule on top of another.
- Refusals that are only nudges can set `CountsTowardRefusalLimit = false`.

Show: README, Policy section, the `Wrap` example ("don't change the tests").

## 6. Humans in the loop, and not (1 min)

- `ApprovalPrompter.Console` for local runs; it declines when stdin is redirected.
- Default is `DeclineAll`: in CI an Ask becomes a refusal, never a hang.
- The time budget pauses while a human decides.

Show: `Policies/ApprovalPrompters.cs`.

## 7. Budgets and stop rules (1 min)

- Defaults: 10 minutes of working time, 80 tool calls, 5 refusals.
- A stop is data (`reply.StopReason`), not an exception. Provider failures do throw.
- Stop rules see every event: "more than 6 builds" is three lines.

Show: README, Limits section, the stop rule example.

## 8. Seeing everything (1 min)

- Every step is a typed `AgentEvent`: tool calls, refusals, approvals, usage, the model actually served.
- `ConsoleAgentObserver` for a quick look; `AgentObserver.From` for logs, UIs, metrics.
- OpenTelemetry: `AddSource(AgentTelemetry.SourceName)` gives a span per session and token/tool-call counters.
- `ModelServed.IsFallback` catches the provider silently serving a different model.

Show: README, the events table.

## 9. Our own tools and typed answers (1 min)

- `AgentTool.Create(func, "name", "description")`; parameters described by `[Description]`.
- `requiresApproval: true` sends each call through the policy. UpgradeAgent's `push_branch` works this way.
- `AskAsync<Summary>`: the JSON schema comes from the record; a bad reply is an `Error`, not a crash.
- `validate:` adds checks JSON can't express (non-empty strings, ranges).

Show: `Program.cs`, `list_todos` and the `Summary` record.

## 10. Testing without a model (1 min)

- `ScriptedBackend` plays a script through the real session: policy, prompter, limits, observers.
- Assert on `backend.Decisions`: what was allowed, what was refused and with which feedback.
- Deterministic, no network, runs in milliseconds.

Show: README, "Testing your agent without a model".

## 11. What it is not (1 min)

- Not a sandbox. The shell is real and runs as you. The policy keeps an honest agent on task.
- Some "read-only" programs can write or execute with the right flags; trim `ReadOnlyCommands` to what you need.
- The harness strips secret env vars and turns off repo-driven config. It does not verify the agent's work.
- Verify independently: UpgradeAgent rebuilds, retests and runs guardrails before it commits anything.

Show: README, Security model.

## 12. Live demo (3 min)

1. Offline, no login needed:

   ```sh
   dotnet run --project samples/HelloAgent -- --scripted
   ```

   Point out, in order: `model:` line, `$ ls src` and `· view` (approved reads), `list_todos` (our tool),
   `⊘ refused curl` with its feedback, `✎ src/Greeter.cs` (auto-approved `.cs` edit), the approval prompt for
   `README.md` (answer `y`), `$ dotnet build` (the `Commands` rule), the summary line, the stats line.
   Run it again with `< /dev/null` to show the prompt declining itself.

2. Real Copilot run, on a throwaway copy of a small repo:

   ```sh
   git clone <small repo> /tmp/demo-repo
   dotnet run --project samples/HelloAgent -- /tmp/demo-repo "Find a TODO in the C# code and resolve it. Keep the build green."
   ```

   Point out: the `CheckAsync` line (try it signed out first for the friendly message), the served model, which
   actions ran on their own and which asked, the summary, and the token count in the stats.

Fallback if the network or login fails: stay on the scripted run and the README.

## 13. Adopting it in your project

1. Copy `src/AgentHarness` into your repo and add a `ProjectReference`. No package edits needed, with or without
   central package management.
2. Sign in once with `copilot` then `/login` (or set a token variable for CI).
3. Start from `samples/HelloAgent/Program.cs`. Change `Instructions`, the `Commands` rules and the
   `AutoApprovedEditExtensions` to fit your task.
4. Run it with `ApprovalPrompter.Console` and watch the refusals. Tighten or loosen the policy until the agent
   finishes without asking for things it shouldn't need.
5. Write `ScriptedBackend` tests for the policy decisions you care about.
6. Before running unattended: default prompter (`DeclineAll`), a disposable checkout, a log observer, and your own
   build and tests on the result.
