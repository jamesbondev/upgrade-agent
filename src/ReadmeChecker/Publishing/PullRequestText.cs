using System.Globalization;
using System.Text;
using ReadmeChecker.Agent;
using ReadmeChecker.Detection;

namespace ReadmeChecker.Publishing;

internal static class PullRequestText
{
    public const string Title = "Update the README to match the repository";

    public static string CommitMessage(string readmePath, IReadOnlyList<ReadmeIssue> issues, IReadOnlyList<Signal> certain)
    {
        var builder = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"docs: update {readmePath} to match the repository")
            .AppendLine();
        foreach (var issue in issues)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {issue.Kind}: {OneLine(issue.SuggestedFix, 100)}");
        }

        foreach (var signal in certain)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Broken link: {OneLine(signal.Text, 100)}");
        }

        return builder.AppendLine().AppendLine("Written by ReadmeChecker's agent and checked by script; review before merging.").ToString();
    }

    public static string Description(
        string readmePath, IReadOnlyList<ReadmeIssue> issues, IReadOnlyList<Signal> certain, string? agentSummary, IReadOnlyList<ReadmeIssue>? unchanged = null)
    {
        var left = unchanged ?? [];
        var builder = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"`{Escape(readmePath)}` no longer matched the repository. An agent (ReadmeChecker) found the problems below and edited the README; a script then checked that only the README changed, that it refers only to files and folders in the repository, that broken links are gone and that it links to no new sites.")
            .AppendLine()
            .AppendLine("**The agent's text is unverified: check each change against the code before merging.**")
            .AppendLine()
            .AppendLine("## What was out of date")
            .AppendLine();

        var number = 1;
        foreach (var issue in issues.Except(left))
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"{number++}. **{issue.Kind}**: \"{Escape(issue.Quote)}\". {Escape(issue.SuggestedFix)}");
            if (issue.Truth is { } truth)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"   The code says: {Escape(truth)}{(issue.Evidence.Count == 0 ? "" : $" ({Escape(string.Join(", ", issue.Evidence))})")}");
            }
        }

        foreach (var signal in certain)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"{number++}. **Broken link** on line {signal.Line}: {Escape(signal.Text)}");
        }

        if (left.Count > 0)
        {
            builder.AppendLine().AppendLine("## Left unchanged").AppendLine()
                .AppendLine("The checker flagged these, but the fix didn't change them; check them by hand.").AppendLine();
            foreach (var issue in left)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- \"{Escape(issue.Quote)}\"{(issue.Truth is { } truth ? $": the checker says {Escape(truth)}" : "")}");
            }
        }

        if (!string.IsNullOrWhiteSpace(agentSummary))
        {
            builder.AppendLine().AppendLine("## What the agent says it changed").AppendLine();
            foreach (var line in agentSummary.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {Escape(line.TrimStart('-', '*', ' '))}");
            }
        }

        return builder.ToString();
    }

    internal static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ReplaceLineEndings(" "))
        {
            if ("\\`*_[]<>|#!".Contains(c, StringComparison.Ordinal))
            {
                builder.Append('\\');
            }

            builder.Append(c);
            if (c == '@')
            {
                builder.Append('​');
            }
        }

        return builder.ToString();
    }

    private static string OneLine(string text, int max)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }
}
