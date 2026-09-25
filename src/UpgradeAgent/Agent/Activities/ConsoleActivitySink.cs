using AgentHarness;
using Spectre.Console;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Ui;

namespace UpgradeAgent.Agent.Activities;

internal sealed class ConsoleActivitySink(SynchronizedConsole console) : IActivitySink
{
    public void Write(ActivityEvent activity)
    {
        var markup = Render(activity);
        if (markup is not null)
        {
            console.Write(c => c.MarkupLine(markup));
        }
    }

    internal static string? Render(ActivityEvent activity) => activity switch
    {
        Note note => $"  [grey]{Markup.Escape(note.Text)}[/]",
        AgentMessage message => $"  [grey italic]{Markup.Escape(message.Text.Truncate(130))}[/]",
        ToolStarted { Kind: ToolKind.Shell } tool => $"    [blue]$[/] {Markup.Escape(tool.Detail.Truncate(120))}",
        ToolStarted { Kind: ToolKind.Edit } tool => $"    [yellow]✎[/] {Markup.Escape(tool.Detail.Truncate(120))}",
        ToolStarted tool => $"    [grey]· {Markup.Escape(tool.Tool)} {Markup.Escape(tool.Detail.Truncate(110))}[/]",
        ToolFailed failed => $"      [red]✗ {Markup.Escape(failed.Error.Truncate(140))}[/]",
        ActionRefused refused => $"    [red]⊘ refused[/] [grey]{Markup.Escape(refused.Action.Truncate(80))} — {Markup.Escape(refused.Reason.Truncate(90))}[/]",
        BuildChecked { Errors: 0 } => "      [green]→ build succeeded[/]",
        BuildChecked build => $"      [red]→ build: {build.Errors} error(s)[/] [grey]{Markup.Escape(build.TopCodes)}[/]",
        TestsChecked { Failed: 0 } tests => $"      [green]→ tests: {tests.Passed} passed[/]",
        TestsChecked tests => $"      [red]→ tests: {tests.Failed} failed[/], {tests.Passed} passed",
        _ => null,
    };
}
