using TestHardener.Analysis;
using TestHardener.Tests.TestSupport;

namespace TestHardener.Tests.Analysis;

public class MemberLocatorTests
{
    private readonly MemberLocator _locator = MemberLocator.Parse(Samples.Calculator);

    [Theory]
    [InlineData("a > b", "Calculator.Add(int, int)", "method")]
    [InlineData("n * 2", "Calculator.Add(int, int)", "method")]
    [InlineData("\"calc\"", "Calculator.Name.get", "accessor")]
    [InlineData("10 - 1", "Calculator.Limit.get", "accessor")]
    [InlineData("seed > 0", "Calculator.Calculator(int)", "constructor")]
    [InlineData("s.Length > 3", "Calculator.Inner.Check(string)", "method")]
    public void Locate_FindsTheEnclosingMember(string snippet, string member, string kind)
    {
        var place = _locator.Locate(SourceLocation.Of(Samples.Calculator, snippet));

        Assert.Equal(member, place.Member?.Name);
        Assert.Equal(kind, place.Member?.Kind);
        Assert.Equal(snippet, place.OriginalText);
    }

    [Fact]
    public void Locate_StaticFieldInitializer_HasNoMember()
    {
        var place = _locator.Locate(SourceLocation.Of(Samples.Calculator, "\"alpha\""));

        Assert.Null(place.Member);
    }

    [Fact]
    public void Locate_ArgumentOfALoggingCall_IsMarkedAsLogging()
    {
        var place = _locator.Locate(SourceLocation.Of(Samples.Calculator, "\"bigger {A}\""));

        Assert.True(place.InLoggingCall);
        Assert.False(place.Member!.IsLogging);
    }

    [Theory]
    [InlineData("count > 0 && count < 10", true)]
    [InlineData("count > 100", false)]
    [InlineData("count < -5", false)]
    public void Locate_ConditionThatOnlyGuardsLogging_IsMarkedAsLogging(string condition, bool expected) =>
        Assert.Equal(expected, _locator.Locate(SourceLocation.Of(Samples.Calculator, condition)).InLoggingCall);

    [Fact]
    public void Locate_MemberNamedLogSomething_IsALoggingMember()
    {
        var place = _locator.Locate(SourceLocation.Of(Samples.Calculator, "value + 1"));

        Assert.Equal("Calculator.LogResult(int)", place.Member?.Name);
        Assert.True(place.Member!.IsLogging);
    }

    [Fact]
    public void Locate_ReportsTheMemberLinesAndSpan()
    {
        var member = _locator.Locate(SourceLocation.Of(Samples.Calculator, "a > b")).Member!;
        var lines = Samples.Calculator.Split('\n');

        Assert.StartsWith("    public int Add", lines[member.StartLine - 1], StringComparison.Ordinal);
        Assert.Equal("    }", lines[member.EndLine - 1]);
        Assert.StartsWith("public int Add", Samples.Calculator[member.SpanStart..member.SpanEnd], StringComparison.Ordinal);
    }

    [Fact]
    public void Locate_PositionOutsideTheFile_FindsNothing()
    {
        var place = _locator.Locate(new TestHardener.Stryker.Location(new(999, 1), new(999, 5)));

        Assert.Null(place.Member);
        Assert.Equal("", place.OriginalText);
    }
}
