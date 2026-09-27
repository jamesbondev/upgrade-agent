using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TestHardener.Hardening;

internal sealed record NewTest(string FullyQualifiedName, string Method, bool IsNewRow);

internal sealed record FileCheck(IReadOnlyList<string> Problems, IReadOnlyList<NewTest> NewTests)
{
    public bool Passed => Problems.Count == 0;
}

internal static partial class TestFileChecker
{
    private static readonly (string Pattern, string Why)[] Forbidden =
    [
        (@"\bBindingFlags\b|\.GetMethod\(|\.GetField\(|\.GetProperty\(|\bInternalsVisibleTo\b", "reflection"),
        (@"\bSystem\.Net\b|\bHttpClient\b|\bWebRequest\b|\bSocket\b", "the network"),
        (@"\bProcess\.Start\b|\bProcessStartInfo\b", "starting processes"),
        (@"\bFile\.(Write|Append|Create|Delete|Move|Copy|Open)\w*\(|\bDirectory\.(Create|Delete|Move)\w*\(", "writing files"),
        (@"\bEnvironment\.(GetEnvironmentVariable|SetEnvironmentVariable|GetEnvironmentVariables)\b", "environment variables"),
        (@"\bThread\.Sleep\b|\bTask\.Delay\b", "sleeping"),
        (@"\bDateTime(Offset)?\.(Now|UtcNow|Today)\b", "the real clock"),
        (@"\bGuid\.NewGuid\b|\bnew Random\(|\bRandom\.Shared\b", "randomness"),
    ];

    public static FileCheck Check(string? before, string after, string? testNamePattern)
    {
        var problems = new List<string>();
        var afterRoot = CSharpSyntaxTree.ParseText(after).GetCompilationUnitRoot();
        var beforeRoot = CSharpSyntaxTree.ParseText(before ?? "").GetCompilationUnitRoot();

        CheckUsings(beforeRoot, afterRoot, problems);
        var beforeTypes = Types(beforeRoot);
        var afterTypes = Types(afterRoot);
        var newTests = new List<NewTest>();

        foreach (var (key, beforeType) in beforeTypes)
        {
            if (!afterTypes.TryGetValue(key, out var afterType))
            {
                problems.Add($"Don't remove or rename the type {key}.");
                continue;
            }

            if (!SyntaxFactory.AreEquivalent(Header(beforeType), Header(afterType)))
            {
                problems.Add($"Don't change the declaration of {key} (its attributes, modifiers, base types or parameters).");
            }

            CompareMembers(key, beforeType, afterType, problems, newTests);
        }

        foreach (var (key, afterType) in afterTypes.Where(t => !beforeTypes.ContainsKey(t.Key)))
        {
            foreach (var member in afterType.Members.Where(m => m is not BaseTypeDeclarationSyntax))
            {
                CheckAdded(key, member, problems, newTests);
            }
        }

        if (CommentCount(afterRoot) > CommentCount(beforeRoot))
        {
            problems.Add("Don't add comments. Say what a test checks with its name.");
        }

        if (newTests.Count == 0)
        {
            problems.Add("Add at least one new [Fact] or [Theory], or a new [InlineData] row on an existing [Theory].");
        }

        if (testNamePattern is not null)
        {
            foreach (var test in newTests.Where(t => !t.IsNewRow && !Regex.IsMatch(t.Method, testNamePattern, RegexOptions.None, TimeSpan.FromSeconds(1))))
            {
                problems.Add($"The test name {test.Method} doesn't follow this repo's convention ({testNamePattern}).");
            }
        }

        return new FileCheck(problems, newTests);
    }

    internal static string FullyQualifiedName(MethodDeclarationSyntax method)
    {
        var types = method.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text);
        var namespaces = method.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString());
        return string.Join('.', namespaces.Concat(types).Append(method.Identifier.Text));
    }

    internal static bool IsTest(MethodDeclarationSyntax method) => method.AttributeLists.SelectMany(l => l.Attributes).Any(IsTestAttribute);

    private static void CompareMembers(string typeKey, TypeDeclarationSyntax beforeType, TypeDeclarationSyntax afterType, List<string> problems, List<NewTest> newTests)
    {
        var beforeMembers = Members(beforeType);
        var afterMembers = Members(afterType);
        foreach (var (key, beforeMember) in beforeMembers)
        {
            if (!afterMembers.TryGetValue(key, out var afterMember))
            {
                problems.Add($"Don't remove or rename {typeKey}.{key}. Add new tests instead of changing existing ones.");
            }
            else if (SyntaxFactory.AreEquivalent(beforeMember, afterMember))
            {
                continue;
            }
            else if (beforeMember is MethodDeclarationSyntax beforeMethod && afterMember is MethodDeclarationSyntax afterMethod
                && OnlyAddsInlineData(beforeMethod, afterMethod))
            {
                newTests.Add(new NewTest(FullyQualifiedName(afterMethod), afterMethod.Identifier.Text, IsNewRow: true));
            }
            else
            {
                problems.Add($"Don't change the existing {typeKey}.{key}. Leave existing tests, helpers and fields as they are; add new ones.");
            }
        }

        foreach (var (key, member) in afterMembers.Where(m => !beforeMembers.ContainsKey(m.Key)))
        {
            CheckAdded(typeKey, member, problems, newTests);
        }
    }

    private static void CheckAdded(string typeKey, MemberDeclarationSyntax member, List<string> problems, List<NewTest> newTests)
    {
        var text = member.WithoutTrivia().ToString();
        foreach (var (pattern, why) in Forbidden)
        {
            if (Regex.IsMatch(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
            {
                problems.Add($"{typeKey}.{Key(member)} uses {why}, which isn't allowed in these tests. Use the repo's fakes and fixed data.");
            }
        }

        if (member is not MethodDeclarationSyntax method || !IsTest(method))
        {
            return;
        }

        if (method.AttributeLists.SelectMany(l => l.Attributes).Any(a => IsTestAttribute(a) && a.ArgumentList?.Arguments.Any(arg => arg.NameEquals?.Name.Identifier.Text == "Skip") == true))
        {
            problems.Add($"{method.Identifier.Text} is skipped. Remove Skip: a skipped test checks nothing.");
        }

        if (!Assertion().IsMatch(method.Body?.ToString() ?? method.ExpressionBody?.ToString() ?? ""))
        {
            problems.Add($"{method.Identifier.Text} has no assertion. Assert the behavior the mutant breaks (Assert.*, Should*, Received/DidNotReceive, Throws*).");
        }

        newTests.Add(new NewTest(FullyQualifiedName(method), method.Identifier.Text, IsNewRow: false));
    }

    private static bool OnlyAddsInlineData(MethodDeclarationSyntax before, MethodDeclarationSyntax after)
    {
        if (!IsTheory(before) || !SyntaxFactory.AreEquivalent(before.WithAttributeLists([]), after.WithAttributeLists([])))
        {
            return false;
        }

        var beforeAttributes = before.AttributeLists.SelectMany(l => l.Attributes).Select(a => a.NormalizeWhitespace().ToString()).ToList();
        var afterAttributes = after.AttributeLists.SelectMany(l => l.Attributes).Select(a => a.NormalizeWhitespace().ToString()).ToList();
        foreach (var attribute in beforeAttributes)
        {
            if (!afterAttributes.Remove(attribute))
            {
                return false;
            }
        }

        return afterAttributes.Count > 0 && afterAttributes.All(a => a.StartsWith("InlineData", StringComparison.Ordinal));
    }

    private static bool IsTheory(MethodDeclarationSyntax method) =>
        method.AttributeLists.SelectMany(l => l.Attributes).Any(a => AttributeName(a).EndsWith("Theory", StringComparison.Ordinal));

    private static bool IsTestAttribute(AttributeSyntax attribute)
    {
        var name = AttributeName(attribute);
        return name.EndsWith("Fact", StringComparison.Ordinal) || name.EndsWith("Theory", StringComparison.Ordinal);
    }

    private static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name switch
        {
            QualifiedNameSyntax qualified => qualified.Right.Identifier.Text,
            SimpleNameSyntax simple => simple.Identifier.Text,
            _ => attribute.Name.ToString(),
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
    }

    private static void CheckUsings(CompilationUnitSyntax before, CompilationUnitSyntax after, List<string> problems)
    {
        var afterUsings = after.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.NormalizeWhitespace().ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var directive in before.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.NormalizeWhitespace().ToString()))
        {
            if (!afterUsings.Contains(directive))
            {
                problems.Add($"Don't remove `{directive}`.");
            }
        }
    }

    private static Dictionary<string, TypeDeclarationSyntax> Types(CompilationUnitSyntax root) =>
        root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .GroupBy(TypeKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static string TypeKey(BaseTypeDeclarationSyntax type)
    {
        var names = type.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text);
        var namespaces = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString());
        return string.Join('.', namespaces.Concat(names));
    }

    private static TypeDeclarationSyntax Header(TypeDeclarationSyntax type) => type.WithMembers([]);

    private static Dictionary<string, MemberDeclarationSyntax> Members(TypeDeclarationSyntax type) =>
        type.Members.Where(m => m is not BaseTypeDeclarationSyntax)
            .GroupBy(Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static string Key(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => $"{m.Identifier.Text}({Parameters(m.ParameterList)})",
        ConstructorDeclarationSyntax c => $"{c.Identifier.Text}({Parameters(c.ParameterList)})",
        PropertyDeclarationSyntax p => p.Identifier.Text,
        IndexerDeclarationSyntax i => $"this[{Parameters(i.ParameterList)}]",
        EventDeclarationSyntax e => e.Identifier.Text,
        BaseFieldDeclarationSyntax f => string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier.Text)),
        _ => member.WithoutTrivia().ToString(),
    };

    private static string Parameters(BaseParameterListSyntax list) =>
        string.Join(", ", list.Parameters.Select(p => p.Type?.ToString() ?? p.Identifier.Text));

    private static int CommentCount(SyntaxNode root) =>
        root.DescendantTrivia(descendIntoTrivia: true).Count(t => t.Kind() is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
            or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia);

    [GeneratedRegex(@"\bAssert\w*\s*[.(]|\.Should\w*\(|\.(Received|DidNotReceive|ReceivedWithAnyArgs|DidNotReceiveWithAnyArgs)\(|\bThrows\w*\s*[<(]|\bVerify\w*\s*\(")]
    private static partial Regex Assertion();
}
