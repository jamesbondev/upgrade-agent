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
        (@"\bBindingFlags\b|\.GetMethod\(|\.GetMethods\(|\.GetField\(|\.GetFields\(|\.GetProperty\(|\.GetProperties\(|\.GetConstructor\(|\.GetRuntime\w+\(|\.InvokeMember\(|\bUnsafeAccessor\b|\bActivator\b|\bInternalsVisibleTo\b|\bdynamic\b", "reflection"),
        (@"\bSystem\.Net\b|\bHttpClient\b|\bWebRequest\b|\bWebClient\b|\bSocket\b|\bTcpClient\b|\bDns\b", "the network"),
        (@"\bProcess\.(Start|GetProcess\w*)\b|\bnew Process\b|\bProcessStartInfo\b", "processes"),
        (@"\bFile\.|\bDirectory\.|\bFileStream\b|\bStreamReader\b|\bStreamWriter\b|\bFileInfo\b|\bDirectoryInfo\b|\bPath\.GetTempPath\b", "the file system"),
        (@"\bEnvironment\.(?!NewLine\b)", "the environment"),
        (@"\bThread\.Sleep\b|\bTask\.Delay\b|\bSpinWait\b", "sleeping"),
        (@"\bDateTime(Offset)?\.(Now|UtcNow|Today)\b|\bStopwatch\b|\bTickCount\b|\bTimeProvider\.System\b", "the real clock"),
        (@"\bGuid\.NewGuid\b|\bnew Random\b|\bRandom\.Shared\b|\bRandomNumberGenerator\b", "randomness"),
    ];

    public static FileCheck Check(string? before, string after, string? testNamePattern)
    {
        var problems = new List<string>();
        var afterRoot = CSharpSyntaxTree.ParseText(after).GetCompilationUnitRoot();
        var beforeRoot = CSharpSyntaxTree.ParseText(before ?? "").GetCompilationUnitRoot();

        CheckFileLevel(beforeRoot, afterRoot, problems);
        var beforeTypes = Types(beforeRoot, problems, reportDuplicates: false);
        var afterTypes = Types(afterRoot, problems, reportDuplicates: true);
        var newTests = new List<NewTest>();

        foreach (var (key, beforeType) in beforeTypes)
        {
            if (!afterTypes.TryGetValue(key, out var afterType))
            {
                problems.Add($"Don't remove or rename the type {key}.");
                continue;
            }

            if (!beforeType.Headers.Zip(afterType.Headers).All(p => SyntaxFactory.AreEquivalent(p.First, p.Second)) || beforeType.Headers.Count != afterType.Headers.Count)
            {
                problems.Add($"Don't change the declaration of {key} (its attributes, modifiers, base types or parameters).");
            }

            CompareMembers(key, beforeType, afterType, problems, newTests);
        }

        foreach (var (key, afterType) in afterTypes.Where(t => !beforeTypes.ContainsKey(t.Key)))
        {
            if (!afterType.IsNested)
            {
                problems.Add($"Don't declare the new top-level type {key}. Add tests to the existing test class; a helper type can be nested inside it.");
            }

            foreach (var header in afterType.Headers)
            {
                CheckForbidden(key, header, problems);
            }

            foreach (var member in afterType.Members.Values)
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
        var types = string.Join('+', method.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text));
        var namespaces = method.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString());
        return string.Join('.', namespaces.Append(types).Append(method.Identifier.Text));
    }

    internal static bool IsTest(MethodDeclarationSyntax method) => method.AttributeLists.SelectMany(l => l.Attributes).Any(IsTestAttribute);

    private static void CheckFileLevel(CompilationUnitSyntax before, CompilationUnitSyntax after, List<string> problems)
    {
        var beforeUsings = Usings(before).ToHashSet(StringComparer.Ordinal);
        var afterUsings = Usings(after).ToList();
        foreach (var directive in beforeUsings.Where(u => !afterUsings.Contains(u, StringComparer.Ordinal)))
        {
            problems.Add($"Don't remove `{directive}`.");
        }

        foreach (var directive in after.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(u => !beforeUsings.Contains(u.NormalizeWhitespace().ToString())
                && (u.GlobalKeyword.IsKind(SyntaxKind.GlobalKeyword) || u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword) || u.Alias is not null)))
        {
            problems.Add($"Don't add `{directive.NormalizeWhitespace()}`. Plain `using Namespace;` directives are fine; global, static and alias usings aren't.");
        }

        if (after.AttributeLists.Count > before.AttributeLists.Count)
        {
            problems.Add("Don't add assembly or module attributes.");
        }

        if (Directives(after) > Directives(before))
        {
            problems.Add("Don't add preprocessor directives (#if, #pragma, #define, #region and the like).");
        }
    }

    private static void CompareMembers(string typeKey, TypeInfo beforeType, TypeInfo afterType, List<string> problems, List<NewTest> newTests)
    {
        foreach (var (key, beforeMember) in beforeType.Members)
        {
            if (!afterType.Members.TryGetValue(key, out var afterMember))
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

        foreach (var (_, member) in afterType.Members.Where(m => !beforeType.Members.ContainsKey(m.Key)))
        {
            CheckAdded(typeKey, member, problems, newTests);
        }
    }

    private static void CheckAdded(string typeKey, MemberDeclarationSyntax member, List<string> problems, List<NewTest> newTests)
    {
        CheckForbidden($"{typeKey}.{Key(member)}", member, problems);
        if (member is MethodDeclarationSyntax { ParameterList.Parameters: [{ Modifiers: var modifiers }, ..] } extension && modifiers.Any(SyntaxKind.ThisKeyword))
        {
            problems.Add($"Don't add the extension method {extension.Identifier.Text}; it can change what existing tests call.");
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

    private static void CheckForbidden(string where, SyntaxNode node, List<string> problems)
    {
        var text = node.WithoutTrivia().NormalizeWhitespace().ToString();
        text = MemberAccessSpacing().Replace(text, ".");
        foreach (var (pattern, why) in Forbidden)
        {
            if (Regex.IsMatch(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
            {
                problems.Add($"{where} uses {why}, which isn't allowed in these tests. Use the repo's fakes and fixed data.");
            }
        }
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

    private static IEnumerable<string> Usings(CompilationUnitSyntax root) =>
        root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.NormalizeWhitespace().ToString());

    private static Dictionary<string, TypeInfo> Types(CompilationUnitSyntax root, List<string> problems, bool reportDuplicates)
    {
        var types = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);
        foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var key = TypeKey(declaration);
            if (!types.TryGetValue(key, out var info))
            {
                info = new TypeInfo(declaration.Parent is TypeDeclarationSyntax);
                types[key] = info;
            }

            info.Headers.Add(declaration.WithMembers([]));
            foreach (var member in declaration.Members.Where(m => m is not BaseTypeDeclarationSyntax))
            {
                var memberKey = Key(member);
                if (!info.Members.TryAdd(memberKey, member) && reportDuplicates)
                {
                    problems.Add($"{key}.{memberKey} is declared more than once. Give each member its own name.");
                }
            }
        }

        return types;
    }

    private static string TypeKey(BaseTypeDeclarationSyntax type)
    {
        var names = type.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t is TypeDeclarationSyntax { TypeParameterList: { } parameters }
            ? $"{t.Identifier.Text}`{parameters.Parameters.Count}"
            : t.Identifier.Text);
        var namespaces = type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString());
        return string.Join('.', namespaces.Concat(names));
    }

    private static string Key(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => $"{m.Identifier.Text}{(m.TypeParameterList is { } generic ? $"<{generic.Parameters.Count}>" : "")}({Parameters(m.ParameterList)})",
        ConstructorDeclarationSyntax c => $"{(c.Modifiers.Any(SyntaxKind.StaticKeyword) ? "static " : "")}{c.Identifier.Text}({Parameters(c.ParameterList)})",
        PropertyDeclarationSyntax p => p.Identifier.Text,
        IndexerDeclarationSyntax i => $"this[{Parameters(i.ParameterList)}]",
        EventDeclarationSyntax e => e.Identifier.Text,
        BaseFieldDeclarationSyntax f => string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier.Text)),
        _ => member.WithoutTrivia().NormalizeWhitespace().ToString(),
    };

    private static string Parameters(BaseParameterListSyntax list) =>
        string.Join(", ", list.Parameters.Select(p => $"{string.Join(' ', p.Modifiers.Select(m => m.Text))} {p.Type}".Trim()));

    private static int CommentCount(SyntaxNode root) =>
        root.DescendantTrivia(descendIntoTrivia: true).Count(t => t.Kind() is SyntaxKind.SingleLineCommentTrivia or SyntaxKind.MultiLineCommentTrivia
            or SyntaxKind.SingleLineDocumentationCommentTrivia or SyntaxKind.MultiLineDocumentationCommentTrivia);

    private static int Directives(SyntaxNode root) =>
        root.DescendantTrivia(descendIntoTrivia: true).Count(t => t.IsDirective || t.IsKind(SyntaxKind.DisabledTextTrivia));

    [GeneratedRegex(@"\bAssert\w*\s*[.(]|\.Should\w*\(|\.(Received|DidNotReceive|ReceivedWithAnyArgs|DidNotReceiveWithAnyArgs)\(|\bThrows\w*\s*[<(]|\bVerify\w*\s*\(")]
    private static partial Regex Assertion();

    [GeneratedRegex(@"\s*\.\s*")]
    private static partial Regex MemberAccessSpacing();

    private sealed class TypeInfo(bool isNested)
    {
        public bool IsNested { get; } = isNested;

        public List<TypeDeclarationSyntax> Headers { get; } = [];

        public Dictionary<string, MemberDeclarationSyntax> Members { get; } = new(StringComparer.Ordinal);
    }
}
