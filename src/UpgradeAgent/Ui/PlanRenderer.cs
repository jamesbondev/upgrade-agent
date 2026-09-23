using Spectre.Console;
using UpgradeAgent.Detection;
using UpgradeAgent.Preflight;

namespace UpgradeAgent.Ui;

/// <summary>Append-only rendering. Nothing here is live, so prompts can safely follow any output.</summary>
public static class PlanRenderer
{
    public static void RenderPreflight(IAnsiConsole console, IReadOnlyList<PreflightCheck> checks)
    {
        console.Write(new Rule("[bold]Preflight[/]").LeftJustified());
        foreach (var check in checks)
        {
            var mark = check.Passed ? "[green]✓[/]" : "[red]✗[/]";
            console.MarkupLine($"  {mark} {Markup.Escape(check.Name)} [grey]{Markup.Escape(check.Detail)}[/]");
        }

        console.WriteLine();
    }

    public static void RenderPlan(IAnsiConsole console, UpgradePlan plan, string repoPath)
    {
        console.Write(new Rule("[bold]Upgrade plan[/]").LeftJustified());

        if (plan.Updates.Count == 0)
        {
            console.MarkupLine("  [green]Everything is up to date.[/]");
            console.WriteLine();
            return;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn(new TableColumn("Package"))
            .AddColumn(new TableColumn("Projects"))
            .AddColumn(new TableColumn("From").NoWrap())
            .AddColumn(new TableColumn("To").NoWrap())
            .AddColumn(new TableColumn("Bump").NoWrap())
            .AddColumn(new TableColumn("Group"))
            .AddColumn(new TableColumn("Decision"));

        foreach (var update in plan.Updates)
        {
            table.AddRow(
                new Markup(Markup.Escape(update.Id)),
                new Markup($"[grey]{Markup.Escape(DescribeProjects(update.Projects, repoPath))}[/]"),
                new Markup(Markup.Escape(update.From)),
                new Markup($"[bold]{Markup.Escape(update.To)}[/]"),
                new Markup(BumpMarkup(update.Kind)),
                new Markup(Markup.Escape(update.Group ?? "")),
                new Markup(DecisionMarkup(update)));
        }

        console.Write(table);

        var planned = plan.Groups.Sum(g => g.Updates.Count);
        console.MarkupLine(
            $"  [bold]{planned}[/] planned in [bold]{plan.Groups.Count}[/] group(s): " +
            string.Join(" → ", plan.Groups.Select(g => $"[blue]{Markup.Escape(g.Name)}[/]")));
        console.WriteLine();
    }

    private static string DescribeProjects(IReadOnlyList<ProjectTarget> projects, string repoPath)
    {
        var names = projects.Select(p => Path.GetFileNameWithoutExtension(p.ProjectPath)).Distinct().ToList();
        var frameworks = projects.Select(p => p.Framework).Distinct().ToList();
        var label = names.Count <= 2 ? string.Join(", ", names) : $"{names.Count} projects";
        return frameworks.Count > 1 ? $"{label} ({string.Join(", ", frameworks)})" : label;
    }

    private static string BumpMarkup(BumpKind kind) => kind switch
    {
        BumpKind.Major => "[red]major[/]",
        BumpKind.Minor => "[yellow]minor[/]",
        _ => "[green]patch[/]",
    };

    private static string DecisionMarkup(PlannedUpdate update)
    {
        var reason = update.Reason is null ? "" : $" [grey]{Markup.Escape(update.Reason)}[/]";
        return update.Decision switch
        {
            UpdateDecision.Planned => $"[green]planned[/]{reason}",
            UpdateDecision.Manual => $"[yellow]manual[/]{reason}",
            UpdateDecision.NeedsTfmUpgrade => $"[fuchsia]needs TFM upgrade[/]{reason}",
            _ => $"[grey]skipped[/]{reason}",
        };
    }
}
