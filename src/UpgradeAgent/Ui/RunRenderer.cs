using Spectre.Console;
using UpgradeAgent.Agent;
using UpgradeAgent.Build;
using UpgradeAgent.Bumping;
using UpgradeAgent.Detection;
using UpgradeAgent.Guardrails;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;
using UpgradeAgent.Workspace;

namespace UpgradeAgent.Ui;

/// <summary>The console view of a run. Append-only: every call writes new lines and never redraws, so prompts are safe anywhere.</summary>
internal sealed class RunRenderer(IAnsiConsole console, PlanRenderer plans) : IRunProgress
{
    private const int MaxErrorsShown = 8;
    private const int OutputTailLines = 15;

    public void Status(string message) => console.MarkupLine($"[grey]{Markup.Escape(message)}[/]");

    public Task<T> WithSpinnerAsync<T>(string message, Func<Task<T>> action) =>
        console.Profile.Capabilities.Interactive
            ? console.Status().Spinner(Spinner.Known.Dots).StartAsync(Markup.Escape(message), _ => action())
            : LogThenRunAsync(message, action);

    public void WorkspaceReady(RunWorkspace workspace)
    {
        console.Write(new Rule("[bold]Run[/]").LeftJustified());
        console.MarkupLine($"  Branch    [blue]{Markup.Escape(workspace.BranchName)}[/]");
        console.MarkupLine($"  Worktree  [grey]{Markup.Escape(workspace.WorktreePath)}[/]");
        console.MarkupLine($"  Output    [grey]{Markup.Escape(workspace.OutputDirectory)}[/]");
        console.WriteLine();
    }

    public void BaselineReady(Baseline baseline, bool fromCache)
    {
        var source = fromCache ? "cached" : "measured";
        var detail = baseline.Tests is not null
            ? $"{baseline.Tests.Passed} tests passed across {baseline.Tests.Methods.Count} test methods"
            : $"{baseline.PassedCount} tests passed (no TRX: count-only)";
        console.MarkupLine($"[green]✓[/] Baseline ({source}, {Markup.Escape(baseline.Commit.ShortSha())}): {Markup.Escape(detail)}");
        console.WriteLine();
    }

    public void PlanReady(UpgradePlan plan) => plans.Plan(plan);

    public void GroupStarted(UpdateGroup group, int index, int count)
    {
        var kind = group.Kind == GroupKind.Major ? "[red]major[/]" : "[green]patch/minor[/]";
        console.Write(new Rule($"[bold]Group {index}/{count}: {Markup.Escape(group.Name)}[/] ({kind})").LeftJustified());
    }

    public void Bumped(BumpResult bump)
    {
        foreach (var edit in bump.Edits)
        {
            console.MarkupLine($"  [blue]bump[/] {Markup.Escape(edit.Id)} {edit.From} → [bold]{edit.To}[/] [grey]{Markup.Escape(edit.File)}[/]");
        }

        foreach (var manual in bump.Manual)
        {
            console.MarkupLine($"  [yellow]manual[/] {Markup.Escape(manual.Update.Id)}: {Markup.Escape(manual.Reason)}");
        }
    }

    public void Built(string label, BuildResult build)
    {
        var seconds = $"{build.Duration.TotalSeconds:0.0}s";
        if (build.Succeeded)
        {
            var warnings = build.Warnings.Count == 0 ? "" : $", {build.Warnings.Count} warning(s) [grey]({Markup.Escape(BuildOutputParser.TopCodes(build.Warnings, 3))})[/]";
            console.MarkupLine($"  [green]✓[/] {label} succeeded [grey]{seconds}[/]{warnings}");
            return;
        }

        console.MarkupLine($"  [red]✗[/] {label} failed [grey]{seconds}[/]: {build.Errors.Count} error(s)");
        if (build.Errors.Count == 0)
        {
            console.WriteLine(string.Join(Environment.NewLine, build.Output.TailLines(OutputTailLines)));
            return;
        }

        var table = new Table().Border(TableBorder.Simple).AddColumn("Code").AddColumn("Location").AddColumn("Message");
        foreach (var error in build.Errors.Take(MaxErrorsShown))
        {
            var location = error.File is null ? "" : $"{Path.GetFileName(error.File)}{(error.Line is { } line ? $":{line}" : "")}";
            table.AddRow(
                new Markup($"[red]{Markup.Escape(error.Code)}[/]"),
                new Markup(Markup.Escape(location)),
                new Markup(Markup.Escape(error.Message.Truncate(110))));
        }

        console.Write(table);
        if (build.Errors.Count > MaxErrorsShown)
        {
            console.MarkupLine($"  [grey]… {build.Errors.Count - MaxErrorsShown} more[/]");
        }
    }

    public void Tested(TestRunResult tests)
    {
        var seconds = $"{tests.Duration.TotalSeconds:0.0}s";
        console.MarkupLine(tests.Succeeded
            ? $"  [green]✓[/] Tests passed: {tests.Passed} [grey]{seconds}[/]"
            : $"  [red]✗[/] Tests failed: {tests.Failed} failed, {tests.Passed} passed [grey]{seconds}[/]");
    }

    public void Fixed(FixOutcome fix)
    {
        if (!fix.Attempted)
        {
            console.MarkupLine($"  [grey]agent: {Markup.Escape(fix.Summary)}[/]");
            return;
        }

        console.MarkupLine($"  [blue]agent[/]{(fix.Replayed ? " [yellow](replayed)[/]" : "")} {Markup.Escape(fix.Summary)}");
        foreach (var package in fix.Details?.Packages ?? [])
        {
            console.MarkupLine($"    [bold]{Markup.Escape(package.Id)}[/] {Markup.Escape(package.From)} → {Markup.Escape(package.To)} [grey]({Markup.Escape(package.Status.Label())})[/]");
            foreach (var change in package.BreakingChanges)
            {
                console.MarkupLine($"      [red]breaking[/] {Markup.Escape(change)}");
            }

            foreach (var applied in package.Fixes)
            {
                console.MarkupLine($"      [green]fixed[/]    {Markup.Escape(applied.File)} [grey]— {Markup.Escape(applied.Reason)}[/]");
            }

            foreach (var item in package.Unresolved)
            {
                console.MarkupLine($"      [yellow]unresolved[/] {Markup.Escape(item)}");
            }
        }

        console.MarkupLine("  [grey]Re-checking independently: the agent's own build and test results are not trusted.[/]");
    }

    public void GuardrailsChecked(GuardrailReport report)
    {
        console.MarkupLine("  [bold]Guardrails[/]");
        foreach (var check in report.Checks)
        {
            var mark = check.Passed ? "[green]✓[/]" : "[red]✗[/]";
            console.MarkupLine($"    {mark} {Markup.Escape(check.Name)} [grey]{Markup.Escape(check.Detail)}[/]");
        }

        foreach (var note in report.Notes)
        {
            console.MarkupLine($"    [yellow]![/] {Markup.Escape(note.Message)}");
        }
    }

    public void GroupFinished(GroupResult result)
    {
        var line = result.Status switch
        {
            GroupStatus.Accepted => $"[green]Accepted[/] → commit [blue]{Markup.Escape(result.Commit!.ShortSha())}[/]",
            GroupStatus.NothingToDo => $"[grey]Nothing to do:[/] {Markup.Escape(result.Reason ?? "")}",
            GroupStatus.Cancelled => $"[yellow]Cancelled:[/] {Markup.Escape(result.Reason ?? "")}",
            _ => $"[red]Rejected and reverted:[/] {Markup.Escape(result.Reason ?? "")}",
        };
        console.MarkupLine($"  {line} [grey]({result.Duration.TotalSeconds:0}s)[/]");
        console.WriteLine();
    }

    public void RunFinished(RunReport report, IReadOnlyList<string> commits)
    {
        console.Write(new Rule("[bold]Summary[/]").LeftJustified());
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Group").AddColumn("Status").AddColumn("Changes").AddColumn("Detail");
        foreach (var group in report.Groups)
        {
            var color = group.Status switch
            {
                GroupStatus.Accepted => "green",
                GroupStatus.Rejected => "red",
                GroupStatus.Cancelled => "yellow",
                _ => "grey",
            };
            table.AddRow(
                new Markup(Markup.Escape(group.Name)),
                new Markup($"[{color}]{group.Status.Label()}[/]"),
                new Markup(Markup.Escape(string.Join(Environment.NewLine, group.Changes))),
                new Markup(Markup.Escape(group.Status == GroupStatus.Accepted ? group.Commit!.ShortSha() : (group.Reason ?? "").Truncate(90))));
        }

        console.Write(table);
        console.MarkupLine($"  Branch [blue]{Markup.Escape(report.Branch)}[/] · {report.Ledger.Count} commit(s) · {report.Duration.TotalSeconds:0}s");
        foreach (var line in commits)
        {
            console.MarkupLine($"    [grey]{Markup.Escape(line)}[/]");
        }

        console.WriteLine();
    }

    private async Task<T> LogThenRunAsync<T>(string message, Func<Task<T>> action)
    {
        Status(message);
        return await action();
    }
}
