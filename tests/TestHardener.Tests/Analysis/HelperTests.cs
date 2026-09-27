using TestHardener.Analysis;

namespace TestHardener.Tests.Analysis;

public class GlobTests
{
    [Theory]
    [InlineData("**/Prompts/**", "src/Engine/Prompts/Builder.cs", true)]
    [InlineData("**/Prompts/**", "Prompts/Builder.cs", true)]
    [InlineData("**/Prompts/**", "src/Engine/PromptsX/Builder.cs", false)]
    [InlineData("src/*.cs", "src/A.cs", true)]
    [InlineData("src/*.cs", "src/sub/A.cs", false)]
    [InlineData("src/**/*.cs", "src/A.cs", true)]
    [InlineData("src/**/*.cs", "src/a/b/A.cs", true)]
    [InlineData("src/?.cs", "src/A.cs", true)]
    [InlineData("src\\*.cs", "src/A.cs", true)]
    [InlineData("a.b/*.cs", "aXb/A.cs", false)]
    public void IsMatch_FollowsGlobRules(string pattern, string path, bool expected) =>
        Assert.Equal(expected, Glob.IsMatch(pattern, path));
}

public class FixHistoryTests
{
    [Theory]
    [InlineData("fix: handle nulls", true)]
    [InlineData("fix(engine): handle nulls", true)]
    [InlineData("fix(engine)!: breaking", true)]
    [InlineData("Fix: capitalised", true)]
    [InlineData("hotfix: prod", true)]
    [InlineData("Revert \"feat: x\"", true)]
    [InlineData("feat: fix things", false)]
    [InlineData("fixture: add data", false)]
    [InlineData("refactor(fix): no", false)]
    public void IsFix_RecognisesFixSubjects(string subject, bool expected) =>
        Assert.Equal(expected, FixHistory.IsFix(subject));

    [Fact]
    public void Parse_CountsFixCommitsPerFile()
    {
        var output = "\u001efix: one\n\nsrc/A.cs\nsrc/B.cs\n\u001efeat: two\n\nsrc/A.cs\n\u001efix(x): three\n\nsrc/A.cs\n";

        var counts = FixHistory.Parse(output);

        Assert.Equal(2, counts["src/A.cs"]);
        Assert.Equal(1, counts["src/B.cs"]);
    }
}
