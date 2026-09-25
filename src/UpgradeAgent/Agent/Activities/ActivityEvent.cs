using System.Text.Json.Serialization;
using AgentHarness;

namespace UpgradeAgent.Agent.Activities;

/// <summary>
/// What an agent session did, as data. Sinks decide how to show it (console, log file, recording), so a
/// recording replays through the same rendering as a live session. Paths in events are repository-relative.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Note), "note")]
[JsonDerivedType(typeof(AgentMessage), "message")]
[JsonDerivedType(typeof(ToolStarted), "tool")]
[JsonDerivedType(typeof(ToolFailed), "toolFailed")]
[JsonDerivedType(typeof(ActionRefused), "refused")]
[JsonDerivedType(typeof(BuildChecked), "build")]
[JsonDerivedType(typeof(TestsChecked), "tests")]
[JsonDerivedType(typeof(Transcript), "transcript")]
internal abstract record ActivityEvent;

/// <summary>A line from the app itself (budget, served model, why the session stopped).</summary>
internal sealed record Note(string Text) : ActivityEvent;

/// <summary>Text the model wrote between tool calls.</summary>
internal sealed record AgentMessage(string Text) : ActivityEvent;

/// <param name="Kind">The harness's own enum: its member names are what recordings store, so they must not change.</param>
internal sealed record ToolStarted(ToolKind Kind, string Tool, string Detail) : ActivityEvent;

internal sealed record ToolFailed(string Error) : ActivityEvent;

internal sealed record ActionRefused(string Action, string Reason) : ActivityEvent;

/// <summary>The outcome of a build the agent ran, read from its output.</summary>
internal sealed record BuildChecked(int Errors, string TopCodes) : ActivityEvent;

/// <summary>The outcome of a test run the agent ran, read from its output.</summary>
internal sealed record TestsChecked(int Passed, int Failed) : ActivityEvent;

/// <summary>Full prompts and replies: kept in the log file, never shown on the console.</summary>
internal sealed record Transcript(string Label, string Text) : ActivityEvent;

/// <summary>An event and when it happened, relative to the start of the session.</summary>
internal sealed record RecordedActivity(double OffsetSeconds, ActivityEvent Event);
