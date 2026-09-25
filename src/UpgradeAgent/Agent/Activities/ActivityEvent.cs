using System.Text.Json.Serialization;
using AgentHarness;

namespace UpgradeAgent.Agent.Activities;

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

internal sealed record Note(string Text) : ActivityEvent;

internal sealed record AgentMessage(string Text) : ActivityEvent;

internal sealed record ToolStarted(ToolKind Kind, string Tool, string Detail) : ActivityEvent;

internal sealed record ToolFailed(string Error) : ActivityEvent;

internal sealed record ActionRefused(string Action, string Reason) : ActivityEvent;

internal sealed record BuildChecked(int Errors, string TopCodes) : ActivityEvent;

internal sealed record TestsChecked(int Passed, int Failed) : ActivityEvent;

internal sealed record Transcript(string Label, string Text) : ActivityEvent;

internal sealed record RecordedActivity(double OffsetSeconds, ActivityEvent Event);
