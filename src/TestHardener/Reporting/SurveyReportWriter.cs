using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TestHardener.Analysis;
using TestHardener.Run;
using TestHardener.Stryker;

namespace TestHardener.Reporting;

internal static class SurveyReportWriter
{
    public const string RepoFileName = "survey.json";

    private const int ShownSurvivors = 12;
    private const int ShownGroups = 50;
    private const int ShownUntested = 10;
    private const int ShownChangeLength = 80;
    private const int ChangeContext = 15;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string FolderName(string repoName)
    {
        var invalid = Path.GetInvalidFileNameChars().Append('/').Append('\\').ToHashSet();
        var safe = new string(repoName.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c).ToArray()).Trim('.', '-');
        return safe.Length == 0 ? "repo" : safe;
    }

    public static async Task WriteRepoAsync(RepoSurvey survey, string outputDirectory, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(outputDirectory, FolderName(survey.Name));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, RepoFileName), JsonSerializer.Serialize(survey, Json), cancellationToken);
    }

    public static async Task<string> WriteAsync(SurveyReport report, int nextGroups, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(report.OutputDirectory);
        await File.WriteAllTextAsync(Path.Combine(report.OutputDirectory, "report.json"), JsonSerializer.Serialize(report, Json), cancellationToken);
        var markdownPath = Path.Combine(report.OutputDirectory, "report.md");
        await File.WriteAllTextAsync(markdownPath, Markdown(report, nextGroups), cancellationToken);
        return markdownPath;
    }

    internal static string Markdown(SurveyReport report, int nextGroups)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# Mutation survey, run {report.RunId}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{report.Repos.Count} repos in {report.Duration.TotalMinutes:0.0} min.{(report.From is null ? "" : $" Stryker reports reused from {Escape(report.From)}.")}")
            .AppendLine()
            .AppendLine("Candidates are surviving mutants inside a method, constructor or accessor, not in logging and not in static data. Groups are ranked by candidates, then by recent fix commits to the file.")
            .AppendLine()
            .AppendLine("| Repo | Target | Score | Mutants | Survived | Candidates | Groups | Time | Status |")
            .AppendLine("|---|---|---|---|---|---|---|---|---|");

        foreach (var repo in report.Repos)
        {
            if (repo.Targets.Count == 0)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"| {Escape(repo.Name)} | - | - | - | - | - | - | {Minutes(repo.Duration)} | {repo.Status}: {Escape(repo.Note ?? "")} |");
            }

            foreach (var target in repo.Targets)
            {
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"| {Escape(repo.Name)} | {Escape(target.Name)} | {Score(target.Score)} | {target.StatusCounts.Values.Sum()} | {target.Survivors} | {target.Candidates} | {target.Groups.Count} | {Minutes(target.Duration)} | {(target.Failure is null ? "OK" : "Failed")} |");
            }
        }

        foreach (var repo in report.Repos)
        {
            AppendRepo(builder, repo, nextGroups);
        }

        return builder.ToString();
    }

    private static void AppendRepo(StringBuilder builder, RepoSurvey repo, int nextGroups)
    {
        builder.AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"## {Escape(repo.Name)}: {repo.Status}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{Escape(repo.Location)}{(repo.Sha is null ? "" : $" at commit `{repo.Sha}`")}");

        if (repo.Note is not null)
        {
            builder.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Note: {Escape(repo.Note)}");
        }

        foreach (var target in repo.Targets)
        {
            AppendTarget(builder, target, nextGroups);
        }
    }

    private static void AppendTarget(StringBuilder builder, TargetSurvey target, int nextGroups)
    {
        builder.AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"### {Escape(target.Name)}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"`{target.Project}` tested by {string.Join(", ", target.TestProjects.Select(p => $"`{p}`"))}.");

        if (target.Failure is not null)
        {
            builder.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Failed: {Escape(target.Failure)}");
            return;
        }

        builder.AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"Mutation score {Score(target.Score)} over {target.Tests} tests, in {Minutes(target.Duration)}. {Counts(target.StatusCounts)}.")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{target.Candidates} candidates in {target.Groups.Count} groups. Not targeted: {Skipped(target.Skipped)}. Stryker's report: `{target.ReportPath}`.");

        if (target.Groups.Count > 0)
        {
            builder.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"#### Next {Math.Min(nextGroups, target.Groups.Count)} groups").AppendLine();
            foreach (var group in target.Groups.Take(nextGroups))
            {
                AppendGroup(builder, group);
            }

            builder.AppendLine().AppendLine("#### All groups").AppendLine()
                .AppendLine("| Rank | Member | File | Lines | Candidates | Not strings | Fix commits |")
                .AppendLine("|---|---|---|---|---|---|---|");
            foreach (var group in target.Groups.Take(ShownGroups))
            {
                builder.AppendLine(CultureInfo.InvariantCulture,
                    $"| {group.Rank} | {Escape(group.Member.Name)} | {Escape(group.File)} | {group.Member.StartLine}-{group.Member.EndLine} | {group.Survivors.Count} | {group.Survivors.Count(s => !s.IsString)} | {group.FixCommits} |");
            }

            if (target.Groups.Count > ShownGroups)
            {
                builder.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"And {target.Groups.Count - ShownGroups} more groups in survey.json.");
            }
        }

        if (target.NotMutated.Count > 0)
        {
            builder.AppendLine().AppendLine("#### Not mutated: every mutant was a compile error").AppendLine();
            foreach (var member in target.NotMutated)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {Escape(member.Member)} in {Escape(member.File)} ({member.CompileErrors} mutants)");
            }
        }

        if (target.Untested.Count > 0)
        {
            builder.AppendLine().AppendLine("#### Untested code: mutants no test runs").AppendLine();
            foreach (var file in target.Untested.Take(ShownUntested))
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {Escape(file.File)}: {file.Mutants}");
            }

            if (target.Untested.Count > ShownUntested)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- and {target.Untested.Count - ShownUntested} more files");
            }
        }
    }

    private static void AppendGroup(StringBuilder builder, SurvivorGroup group)
    {
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"{group.Rank}. **{Escape(group.Member.Name)}** in {Escape(group.File)}, lines {group.Member.StartLine}-{group.Member.EndLine}: {Escape(string.Join("; ", group.Reasons))}. Covered by {group.CoveringTests.Count} tests.");
        foreach (var survivor in group.Survivors.Take(ShownSurvivors))
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"   - line {survivor.Location.Start.Line}, {Escape(survivor.Mutator)}: {Change(survivor.Original, survivor.Replacement ?? "?")}");
        }

        if (group.Survivors.Count > ShownSurvivors)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"   - and {group.Survivors.Count - ShownSurvivors} more");
        }
    }

    private static string Counts(IReadOnlyDictionary<MutantStatus, int> counts) =>
        string.Join(", ", counts.OrderByDescending(c => c.Value).Select(c => $"{c.Value} {c.Key}"));

    private static string Skipped(IReadOnlyDictionary<SkipReason, int> skipped) =>
        skipped.Count == 0 ? "none" : string.Join(", ", skipped.OrderByDescending(s => s.Value).Select(s => $"{s.Value} {Describe(s.Key)}"));

    private static string Describe(SkipReason reason) => reason switch
    {
        SkipReason.StaticData => "in static data",
        SkipReason.Logging => "in logging",
        SkipReason.IgnoredString => "ignored strings",
        SkipReason.OutsideMember => "outside a method",
        SkipReason.SourceMissing => "in files not found",
        _ => reason.ToString(),
    };

    private static string Score(double? score) => score is { } s ? string.Create(CultureInfo.InvariantCulture, $"{s:0.0}%") : "-";

    private static string Minutes(TimeSpan duration) => string.Create(CultureInfo.InvariantCulture, $"{duration.TotalMinutes:0.0} min");

    internal static string Change(string original, string replacement)
    {
        var from = Flat(original);
        var to = Flat(replacement);
        if (from.Length <= ShownChangeLength && to.Length <= ShownChangeLength)
        {
            return $"`{from}` → `{to}`";
        }

        var prefix = 0;
        while (prefix < from.Length && prefix < to.Length && from[prefix] == to[prefix])
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < from.Length - prefix && suffix < to.Length - prefix && from[^(suffix + 1)] == to[^(suffix + 1)])
        {
            suffix++;
        }

        var start = Math.Max(0, prefix - ChangeContext);
        return $"`{Window(from, start, suffix)}` → `{Window(to, start, suffix)}`";
    }

    private static string Window(string text, int start, int suffix)
    {
        var end = Math.Min(text.Length, Math.Max(start, text.Length - suffix) + ChangeContext);
        var window = text[start..end];
        if (window.Length > ShownChangeLength)
        {
            window = window[..ShownChangeLength];
            end = start + ShownChangeLength;
        }

        return $"{(start > 0 ? "…" : "")}{window}{(end < text.Length ? "…" : "")}";
    }

    private static string Flat(string text) =>
        string.Join(' ', text.ReplaceLineEndings(" ").Split(' ', StringSplitOptions.RemoveEmptyEntries)).Replace("`", "'", StringComparison.Ordinal);

    internal static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ReplaceLineEndings(" "))
        {
            if ("\\`*_[]<>|#".Contains(c, StringComparison.Ordinal))
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
