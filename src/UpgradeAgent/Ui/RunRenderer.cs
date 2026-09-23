using Spectre.Console;
using UpgradeAgent.Build;
using UpgradeAgent.Bumping;
using UpgradeAgent.Detection;
using UpgradeAgent.Guardrails;
using UpgradeAgent.Run;
using UpgradeAgent.Workspace;

namespace UpgradeAgent.Ui;

/// <summary>Append-only run output: every call writes new lines and never redraws, so prompts are safe anywhere.</summary>
public sealed class RunRenderer(IAnsiConsole console)
{
    private const int MaxErrorsShown = 8;

    public void Status(string message) => console.MarkupLine($"[grey]{Markup.Escape(message)}[/]");

    /// <summary>
    /// A spinner for slow phases that never prompt (detection), so a silent pause doesn't look like a hang.
    /// Only used where nothing else writes to the console meanwhile.
    /// </summary>
    public Task<T> WithSpinnerAsync<T>(string message, Func<Task<T>> action) =>
        console.Profile.Capabilities.Interactive
            ? console.Status().Spinner(Spinner.Known.Dots).StartAsync(Markup.Escape(message), _ => action())
            : LogThenRun(message, action);

    private async Task<T> LogThenRun<T>(string message, Func<Task<T>> action)
    {
        Status(message);
        return await action();
    }

    public void PublishHeader(string how)
    {
        console.Write(new Rule("[bold]Publish[/]").LeftJustified());
        console.MarkupLine($"  [grey]{Markup.Escape(how)}[/]");
    }

    public void Published(string descriptionPath, Publishing.PushResult? push)
    {
        if (push is not null)
        {
            var color = push.Pushed ? "green" : push.Refused ? "red" : "yellow";
            console.MarkupLine($"  [{color}]{Markup.Escape(push.Message)}[/]");
        }

        console.MarkupLine($"  PR description: [blue]{Markup.Escape(descriptionPath)}[/]");
        console.WriteLine();
    }

    public void Workspace(RunWorkspace workspace)
    {
        console.Write(new Rule("[bold]Run[/]").LeftJustified());
        console.MarkupLine($"  Branch    [blue]{Markup.Escape(workspace.BranchName)}[/]");
        console.MarkupLine($"  Worktree  [grey]{Markup.Escape(workspace.WorktreePath)}[/]");
        console.MarkupLine($"  Output    [grey]{Markup.Escape(workspace.OutputDirectory)}[/]");
        console.WriteLine();
    }

    public void Baseline(Baseline baseline, bool fromCache)
    {
        var source = fromCache ? "cached" : "measured";
        var detail = baseline.Tests is not null
            ? $"{baseline.Tests.Passed} tests passed across {baseline.Tests.Methods.Count} test methods"
            : $"{baseline.PassedCount} tests passed (no TRX: count-only)";
        console.MarkupLine($"[green]✓[/] Baseline ({source}, {Markup.Escape(baseline.Commit[..8])}): {Markup.Escape(detail)}");
        console.WriteLine();
    }

    public void Plan(UpgradePlan plan, string repoPath) => PlanRenderer.RenderPlan(console, plan, repoPath);

    public void GroupHeader(UpdateGroup group, int index, int count)
    {
        var kind = group.Kind == GroupKind.Major ? "[red]major[/]" : "[green]patch/minor[/]";
        console.Write(new Rule($"[bold]Group {index}/{count}: {Markup.Escape(group.Name)}[/] ({kind})").LeftJustified());
    }

    public void Bump(BumpResult bump)
    {
        foreach (var edit in bump.Edits)
        {
            console.MarkupLine($"  [blue]bump[/] {Markup.Escape(edit.Id)} {Markup.Escape(edit.From)} → [bold]{Markup.Escape(edit.To)}[/] [grey]{Markup.Escape(edit.File)}[/]");
        }

        foreach (var manual in bump.Manual)
        {
            console.MarkupLine($"  [yellow]manual[/] {Markup.Escape(manual.Update.Id)}: {Markup.Escape(manual.Reason)}");
        }
    }

    public void Build(string label, BuildResult build)
    {
        var seconds = $"{build.Duration.TotalSeconds:0.0}s";
        if (build.Succeeded)
        {
            var warnings = build.Warnings.Count == 0 ? "" : $", {build.Warnings.Count} warning(s) [grey]({Markup.Escape(TopCodes(build.Warnings))})[/]";
            console.MarkupLine($"  [green]✓[/] {label} succeeded [grey]{seconds}[/]{warnings}");
            return;
        }

        console.MarkupLine($"  [red]✗[/] {label} failed [grey]{seconds}[/]: {build.Errors.Count} error(s)");
        if (build.Errors.Count == 0)
        {
            console.WriteLine(Tail(build.Output, 15));
            return;
        }

        var table = new Table().Border(TableBorder.Simple).AddColumn("Code").AddColumn("Location").AddColumn("Message");
        foreach (var error in build.Errors.Take(MaxErrorsShown))
        {
            var location = error.File is null ? "" : $"{Path.GetFileName(error.File)}{(error.Line is { } line ? $":{line}" : "")}";
            table.AddRow(
                new Markup($"[red]{Markup.Escape(error.Code)}[/]"),
                new Markup(Markup.Escape(location)),
                new Markup(Markup.Escape(Truncate(error.Message, 110))));
        }

        console.Write(table);
        if (build.Errors.Count > MaxErrorsShown)
        {
            console.MarkupLine($"  [grey]… {build.Errors.Count - MaxErrorsShown} more[/]");
        }
    }

    public void Tests(TestRunResult tests)
    {
        var seconds = $"{tests.Duration.TotalSeconds:0.0}s";
        var passed = tests.Inventory?.Passed ?? tests.Counts?.Passed;
        var failed = tests.Inventory?.Failed ?? tests.Counts?.Failed;
        console.MarkupLine(tests.Succeeded
            ? $"  [green]✓[/] Tests passed: {passed} [grey]{seconds}[/]"
            : $"  [red]✗[/] Tests failed: {failed} failed, {passed} passed [grey]{seconds}[/]");
    }

    public void Fix(FixOutcome fix)
    {
        if (!fix.Attempted)
        {
            console.MarkupLine($"  [grey]agent: {Markup.Escape(fix.Summary)}[/]");
            return;
        }

        console.MarkupLine($"  [blue]agent[/] {Markup.Escape(fix.Summary)}");
        foreach (var package in fix.Details?.Packages ?? [])
        {
            console.MarkupLine($"    [bold]{Markup.Escape(package.Id)}[/] {Markup.Escape(package.From)} → {Markup.Escape(package.To)} [grey]({Markup.Escape(package.Status)})[/]");
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

    public void Guardrails(GuardrailReport report)
    {
        console.MarkupLine("  [bold]Guardrails[/]");
        foreach (var check in report.Checks)
        {
            var mark = check.Passed ? "[green]✓[/]" : "[red]✗[/]";
            console.MarkupLine($"    {mark} {Markup.Escape(check.Name)} [grey]{Markup.Escape(check.Detail)}[/]");
        }

        foreach (var warning in report.Warnings)
        {
            console.MarkupLine($"    [yellow]![/] {Markup.Escape(warning)}");
        }
    }

    public void GroupOutcome(GroupResult result)
    {
        var line = result.Status switch
        {
            GroupStatus.Accepted => $"[green]Accepted[/] → commit [blue]{Markup.Escape(result.Commit![..8])}[/]",
            GroupStatus.NothingToDo => $"[grey]Nothing to do:[/] {Markup.Escape(result.Reason ?? "")}",
            GroupStatus.Cancelled => $"[yellow]Cancelled:[/] {Markup.Escape(result.Reason ?? "")}",
            _ => $"[red]Rejected and reverted:[/] {Markup.Escape(result.Reason ?? "")}",
        };
        console.MarkupLine($"  {line} [grey]({result.Duration.TotalSeconds:0}s)[/]");
        console.WriteLine();
    }

    public void Summary(RunReport report, IReadOnlyList<string> gitLog)
    {
        console.Write(new Rule("[bold]Summary[/]").LeftJustified());
        var table = new Table().Border(TableBorder.Rounded).AddColumn("Group").AddColumn("Status").AddColumn("Changes").AddColumn("Detail");
        foreach (var group in report.Groups)
        {
            var status = group.Status switch
            {
                GroupStatus.Accepted => "[green]accepted[/]",
                GroupStatus.Rejected => "[red]rejected[/]",
                GroupStatus.Cancelled => "[yellow]cancelled[/]",
                _ => "[grey]nothing to do[/]",
            };
            var changes = string.Join(Environment.NewLine, group.Edits.Select(e => $"{e.Id} {e.From} → {e.To}").Distinct());
            table.AddRow(
                new Markup(Markup.Escape(group.Name)),
                new Markup(status),
                new Markup(Markup.Escape(changes)),
                new Markup(Markup.Escape(group.Status == GroupStatus.Accepted ? group.Commit![..8] : Truncate(group.Reason ?? "", 90))));
        }

        console.Write(table);
        console.MarkupLine($"  Branch [blue]{Markup.Escape(report.Branch)}[/] · {report.Ledger.Count} commit(s) · {report.Duration.TotalSeconds:0}s");
        foreach (var line in gitLog)
        {
            console.MarkupLine($"    [grey]{Markup.Escape(line)}[/]");
        }

        console.WriteLine();
    }

    private static string TopCodes(IReadOnlyList<Diagnostic> diagnostics) =>
        string.Join(", ", diagnostics.GroupBy(d => d.Code).OrderByDescending(g => g.Count()).Take(3).Select(g => $"{g.Key}×{g.Count()}"));

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    private static string Tail(string text, int lines) =>
        string.Join(Environment.NewLine, text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).TakeLast(lines));
}
