using System.Text;
using System.Text.RegularExpressions;
using AgentHarness.Policies;
using ReadmeChecker.Detection;
using RepoKit;

namespace ReadmeChecker.Agent;

internal static partial class EvidenceChecks
{
    public const int MinQuoteCharacters = 12;
    public const long MaxEvidenceBytes = 1_000_000;

    private static readonly HashSet<string> CommonWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "not", "are", "with", "from", "that", "this", "only", "also", "uses", "use", "into", "its",
        "has", "have", "each", "one", "two", "three", "four", "five", "var", "new", "public", "private", "return", "await",
        "async", "true", "false", "null", "string", "int", "class", "void", "const", "static", "readonly",
    };

    public static string? QuoteProblem(ReadmeIssue issue, string root, RepoFacts facts)
    {
        var quote = issue.EvidenceQuote ?? "";
        if (quote.Count(c => !char.IsWhiteSpace(c)) < MinQuoteCharacters)
        {
            return $"the evidence quote is shorter than {MinQuoteCharacters} characters";
        }

        var policy = new WorkspacePolicy(root);
        var normalizedQuote = AssessmentValidator.NormalizeText(quote);
        var found = issue.Evidence
            .Select(AssessmentValidator.NormalizePath)
            .OfType<string>()
            .Where(path => !IsMarkdown(path) && !IsTestPath(path))
            .Any(path => ReadEvidence(root, facts, policy, path) is { } text
                && AssessmentValidator.NormalizeText(text).Contains(normalizedQuote, StringComparison.Ordinal));
        if (!found)
        {
            return "the evidence quote isn't in any cited source or config file";
        }

        return issue.Truth is { } truth && !Anchors(truth, quote).Any()
            ? "the evidence quote doesn't contain anything the truth states"
            : null;
    }

    public static async Task<string?> MissingTermProblemAsync(ReadmeIssue issue, string readme, string root, GitCli git, CancellationToken cancellationToken)
    {
        var words = TermWords(issue.MissingTerm ?? "");
        if (words.Count == 0 || string.Concat(words).Length < 3)
        {
            return "the missing term is empty";
        }

        if (!new Regex(string.Join(@"[\s_-]*", words.Select(Regex.Escape)), RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)).IsMatch(readme))
        {
            return "the missing term isn't in the README";
        }

        var pattern = string.Join("[[:space:]_-]*", words.Select(EscapeForGrep));
        var result = await git.TryRunAsync(root, ["grep", "-I", "-i", "-E", "-q", "-e", pattern, "--", ".", ":(exclude)*.md", ":(exclude)*.markdown"], cancellationToken);
        return result.ExitCode switch
        {
            1 => null,
            0 => "the missing term still appears in the code",
            _ => "the code couldn't be searched for the missing term",
        };
    }

    public static IReadOnlyList<string> Anchors(string truth, string quote)
    {
        var quoteTokens = Tokens(quote).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Tokens(truth).Where(quoteTokens.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IEnumerable<string> Tokens(string text) =>
        Token().Matches(text)
            .Select(m => m.Value.Trim('.', ':', '-'))
            .Where(t => t.Length >= 3 && !CommonWords.Contains(t) && IsDistinctive(t));

    public static IReadOnlyList<string> TermWords(string term) =>
        TermWord().Matches(term).Select(m => m.Value).ToList();

    internal static bool IsMarkdown(string path) =>
        path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase);

    internal static bool IsTestPath(string path) =>
        path.Split('/').SkipLast(1).Any(s =>
            s.Equals("test", StringComparison.OrdinalIgnoreCase)
            || s.Equals("tests", StringComparison.OrdinalIgnoreCase)
            || s.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
            || s.EndsWith(".Test", StringComparison.OrdinalIgnoreCase));

    private static string? ReadEvidence(string root, RepoFacts facts, WorkspacePolicy policy, string path)
    {
        if (!facts.IsFile(path))
        {
            return null;
        }

        var full = Path.Combine(root, path);
        if (policy.EvaluateRead(full).Verdict != ToolVerdict.Approve)
        {
            return null;
        }

        try
        {
            var info = new FileInfo(full);
            if (!info.Exists || info.Length > MaxEvidenceBytes)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(full);
            return bytes.Contains((byte)0) ? null : Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsDistinctive(string token) =>
        token.Any(char.IsDigit)
        || token.Skip(1).Any(char.IsUpper)
        || token.IndexOfAny(['.', '-', '_', ':']) > 0
        || token.Length >= 8;

    private static string EscapeForGrep(string word) => Regex.Replace(word, @"[.^$*+?()\[\]{}|\\]", @"\$0");

    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9._:\-]*[A-Za-z0-9]")]
    private static partial Regex Token();

    [GeneratedRegex(@"[A-Z]?[a-z0-9]+|[A-Z]+(?![a-z])")]
    private static partial Regex TermWord();
}
