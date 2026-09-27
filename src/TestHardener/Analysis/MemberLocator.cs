using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using TestHardener.Stryker;
using Location = TestHardener.Stryker.Location;

namespace TestHardener.Analysis;

internal sealed record MemberInfo(string Name, string Kind, int StartLine, int EndLine, int SpanStart, int SpanEnd, bool IsLogging, string TypeName = "")
{
    public string OuterType => TypeName.Split('.')[0];
}

internal sealed record MutantPlace(MemberInfo? Member, bool InLoggingCall, string OriginalText);

internal sealed partial class MemberLocator
{
    private static readonly HashSet<string> TelemetryCalls =
        new(["SetTag", "AddTag", "AddEvent", "AddBaggage", "SetStatus", "StartActivity", "SetBaggage"], StringComparer.Ordinal);

    private readonly SyntaxNode _root;
    private readonly SourceText _text;

    private MemberLocator(SyntaxTree tree)
    {
        _root = tree.GetRoot();
        _text = tree.GetText();
    }

    public static MemberLocator Parse(string source) => new(CSharpSyntaxTree.ParseText(source));

    public MutantPlace Locate(Location location)
    {
        var span = SpanOf(location);
        if (span is not { } resolved)
        {
            return new MutantPlace(null, false, "");
        }

        MemberInfo? member = null;
        var inLogging = false;
        foreach (var node in _root.FindToken(resolved.Start).Parent?.AncestorsAndSelf() ?? [])
        {
            member = Describe(node);
            if (member is not null)
            {
                break;
            }

            inLogging |= node is InvocationExpressionSyntax invocation && IsLoggingCall(invocation);
        }

        var original = _text.ToString(resolved);
        return new MutantPlace(member, inLogging, original.Length > 200 ? original[..200] + "…" : original);
    }

    internal TextSpan? SpanOf(Location location)
    {
        var start = PositionOf(location.Start);
        var end = PositionOf(location.End);
        return start is { } s && end is { } e && e >= s ? TextSpan.FromBounds(s, e) : null;
    }

    private int? PositionOf(Position position)
    {
        var line = position.Line - 1;
        if (line < 0 || line >= _text.Lines.Count)
        {
            return null;
        }

        var textLine = _text.Lines[line];
        var character = position.Column - 1;
        return character < 0 || textLine.Start + character > textLine.EndIncludingLineBreak ? null : textLine.Start + character;
    }

    private MemberInfo? Describe(SyntaxNode node)
    {
        var (name, kind, attributes) = node switch
        {
            MethodDeclarationSyntax m => ($"{m.Identifier.Text}({Parameters(m.ParameterList)})", "method", m.AttributeLists),
            ConstructorDeclarationSyntax c => ($"{c.Identifier.Text}({Parameters(c.ParameterList)})", "constructor", c.AttributeLists),
            OperatorDeclarationSyntax o => ($"operator {o.OperatorToken.Text}({Parameters(o.ParameterList)})", "operator", o.AttributeLists),
            ConversionOperatorDeclarationSyntax c => ($"operator {c.Type}({Parameters(c.ParameterList)})", "operator", c.AttributeLists),
            AccessorDeclarationSyntax { Parent.Parent: BasePropertyDeclarationSyntax property } a => ($"{PropertyName(property)}.{a.Keyword.Text}", "accessor", a.AttributeLists),
            PropertyDeclarationSyntax { ExpressionBody: not null } p => ($"{p.Identifier.Text}.get", "accessor", p.AttributeLists),
            IndexerDeclarationSyntax { ExpressionBody: not null } i => ($"this[{Parameters(i.ParameterList)}].get", "accessor", i.AttributeLists),
            _ => (null, null, default(SyntaxList<AttributeListSyntax>)),
        };

        if (name is null || kind is null)
        {
            return null;
        }

        var typeName = string.Join('.', node.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text));
        var lines = _text.Lines.GetLinePositionSpan(node.Span);
        var memberName = MemberName(node);
        var isLogging = LogMemberName().IsMatch(memberName)
            || attributes.SelectMany(a => a.Attributes).Any(a => a.Name.ToString().EndsWith("LoggerMessage", StringComparison.Ordinal));
        return new MemberInfo(
            typeName.Length == 0 ? name : $"{typeName}.{name}",
            kind,
            lines.Start.Line + 1,
            lines.End.Line + 1,
            node.Span.Start,
            node.Span.End,
            isLogging,
            typeName);
    }

    private static string MemberName(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        AccessorDeclarationSyntax { Parent.Parent: PropertyDeclarationSyntax p } => p.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        _ => "",
    };

    private static string PropertyName(BasePropertyDeclarationSyntax property) => property switch
    {
        PropertyDeclarationSyntax p => p.Identifier.Text,
        IndexerDeclarationSyntax i => $"this[{Parameters(i.ParameterList)}]",
        EventDeclarationSyntax e => e.Identifier.Text,
        _ => "?",
    };

    private static string Parameters(BaseParameterListSyntax list) =>
        string.Join(", ", list.Parameters.Select(p => p.Type?.ToString() ?? p.Identifier.Text));

    private static bool IsLoggingCall(InvocationExpressionSyntax invocation)
    {
        var name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax access => access.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            GenericNameSyntax generic => generic.Identifier.Text,
            MemberBindingExpressionSyntax binding => binding.Name.Identifier.Text,
            _ => "",
        };
        return LogMemberName().IsMatch(name) || TelemetryCalls.Contains(name);
    }

    [GeneratedRegex("^Log($|[A-Z_])")]
    private static partial Regex LogMemberName();
}
