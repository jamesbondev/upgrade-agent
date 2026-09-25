using System.Globalization;
using AgentHarness;
using ReadmeChecker.Config;
using ReadmeChecker.Detection;
using RepoKit;

namespace ReadmeChecker.Agent;

internal sealed class DeepReadmeAssessor(IAgentBackendFactory backends, AgentOptions options, GitCli git, TimeProvider time) : IReadmeAssessor
{
    public async Task<AssessmentOutcome> AssessAsync(
        string repoName, string root, RepoFacts facts, SignalScan scan, string logPath, double? creditBudget, CancellationToken cancellationToken)
    {
        var readme = facts.Readme!;
        var plan = ReadmeChunks.Plan(readme.Text);
        await using var backend = backends.Create();
        await backends.EnsureReadyAsync(backend, root, cancellationToken);
        await using var log = AgentLog.Open(logPath, time);

        var accepted = new List<ReadmeIssue>();
        var rejected = new List<RejectedIssue>();
        var parts = new List<ChunkCoverage>();
        var stats = new List<AgentStats>();
        var summaries = new List<string>();
        var projectChunk = ChunkForProjects(plan, facts);

        foreach (var chunk in plan.Chunks)
        {
            var spent = stats.Sum(s => s.AiCredits);
            if (creditBudget is { } budget && spent >= budget)
            {
                parts.Add(new ChunkCoverage(chunk.Index, chunk.FirstLine, chunk.LastLine, ChunkStatus.Skipped, 0, 0, "the AI credit budget was spent"));
                continue;
            }

            var signals = scan.Signals.Where(s => s.Line >= chunk.FirstLine && s.Line <= chunk.LastLine || (s.Line == 0 && chunk.Index == projectChunk)).ToList();
            log.Note($"--- part {chunk.Index} of {plan.Chunks.Count}: lines {chunk.FirstLine}-{chunk.LastLine}");
            var result = await AgentSessions.ExploreThenAskAsync<ChunkCheck>(
                backend,
                ReadmeAssessor.SessionOptions($"readme deep {repoName} part {chunk.Index}", root, DeepPrompts.System, options, log),
                DeepPrompts.Task(repoName, readme.Path, chunk, plan.Chunks.Count, plan, facts, signals),
                DeepPrompts.Question,
                askAfterStop: true,
                time,
                cancellationToken);

            var credits = result.Stats?.AiCredits ?? 0;
            if (result.Stats is { } sessionStats)
            {
                stats.Add(sessionStats);
            }

            if (result.Structured?.Value is not { } check)
            {
                var why = result.Failure ?? result.Structured?.Error ?? result.Reply?.StopReason ?? "no answer";
                parts.Add(new ChunkCoverage(chunk.Index, chunk.FirstLine, chunk.LastLine, ChunkStatus.NotChecked, 0, credits, why));
                continue;
            }

            var scope = new ValidationScope(root, git, facts, scan.Signals, Deep: true, chunk.FirstLine, chunk.LastLine);
            var validation = await AssessmentValidator.ValidateAsync(check.Problems, scope, cancellationToken);
            accepted.AddRange(validation.Accepted);
            rejected.AddRange(validation.Rejected);
            summaries.Add($"Lines {chunk.FirstLine}-{chunk.LastLine}: {check.Summary}");
            var status = result.Reply is { Stopped: true } ? ChunkStatus.PartlyChecked : ChunkStatus.Checked;
            parts.Add(new ChunkCoverage(chunk.Index, chunk.FirstLine, chunk.LastLine, status, check.ClaimsChecked, credits, result.Reply?.StopReason));
        }

        var notChecked = plan.Unchecked.Select(u => new LineRange(u.FirstLine, u.LastLine)).ToList();
        var coverage = new Coverage(parts.Sum(p => p.ClaimsChecked), parts, notChecked);
        var complete = parts.All(p => p.Status == ChunkStatus.Checked) && notChecked.Count == 0;
        var verdict = accepted.Count > 0 ? AssessedVerdict.Stale : complete ? AssessedVerdict.Current : AssessedVerdict.Unsure;
        var answered = parts.Count(p => p.Status is ChunkStatus.Checked or ChunkStatus.PartlyChecked);
        var failure = answered == 0 ? $"no part of the README could be checked: {parts.FirstOrDefault()?.Note ?? "no parts"}" : null;
        var summary = string.Create(CultureInfo.InvariantCulture, $"Checked {coverage.ClaimsChecked} claims in {answered} of {plan.Chunks.Count + notChecked.Count} parts of the README.")
            + (summaries.Count == 0 ? "" : " " + string.Join(" ", summaries));

        return new AssessmentOutcome(failure is null ? verdict : null, accepted, rejected, summary, AgentSessions.Sum(stats), failure, coverage);
    }

    internal static int ChunkForProjects(ChunkPlan plan, RepoFacts facts)
    {
        var names = facts.Projects
            .Where(p => !p.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) && !p.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return plan.Chunks
            .Select(c => (c.Index, Mentions: names.Count(n => c.Text.Contains(n, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(c => c.Mentions)
            .ThenBy(c => c.Index)
            .Select(c => c.Index)
            .FirstOrDefault(1);
    }
}
