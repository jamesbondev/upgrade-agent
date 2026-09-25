using Spectre.Console;
using UpgradeAgent.Publishing;
using UpgradeAgent.Replay;

namespace UpgradeAgent.Ui;

/// <summary>Console output around a run: replay banners, publishing, Azure DevOps and recordings.</summary>
internal sealed class PublishRenderer(IAnsiConsole console)
{
    public void AzureDevOpsVerified(string repository)
    {
        console.MarkupLine($"  [green]✓[/] Azure DevOps [grey]{Markup.Escape(repository)}[/]");
        console.WriteLine();
    }

    public void ReplayStarting(RecordingHeader header, string? warning)
    {
        if (warning is not null)
        {
            console.MarkupLine($"[yellow]warning:[/] {Markup.Escape(warning)}");
        }

        console.MarkupLine($"[black on yellow] REPLAY [/] [yellow]'{Markup.Escape(header.Name)}', recorded {header.RecordedUtc:yyyy-MM-dd HH:mm} UTC. Agent sessions are played back; bumps, builds, tests and guardrails run live.[/]");
        console.WriteLine();
    }

    public void PublishHeader(string how)
    {
        console.Write(new Rule("[bold]Publish[/]").LeftJustified());
        console.MarkupLine($"  [grey]{Markup.Escape(how)}[/]");
    }

    public void Published(string descriptionPath, PushResult? push)
    {
        if (push is not null)
        {
            var color = push.Outcome switch
            {
                PushOutcome.Pushed => "green",
                PushOutcome.Refused or PushOutcome.Failed => "red",
                _ => "yellow",
            };
            console.MarkupLine($"  [{color}]{Markup.Escape(push.Message)}[/]");
        }

        console.MarkupLine($"  PR description: [blue]{Markup.Escape(descriptionPath)}[/]");
        console.WriteLine();
    }

    public void PullRequestCreated(string label, string url)
    {
        console.MarkupLine($"  [green]✓[/] Draft PR with label [blue]{Markup.Escape(label)}[/]: [link]{Markup.Escape(url)}[/]");
        console.WriteLine();
    }

    public void Recorded(string directory) => console.MarkupLine($"[grey]Recorded to {Markup.Escape(directory)}[/]");
}
