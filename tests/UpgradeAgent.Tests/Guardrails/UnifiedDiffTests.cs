using UpgradeAgent.Guardrails;

namespace UpgradeAgent.Tests.Guardrails;

public class UnifiedDiffTests
{
    [Fact]
    public void ParsesGitDiffOutput()
    {
        const string Diff = """
            diff --git a/src/App/Code.cs b/src/App/Code.cs
            index 1111111..2222222 100644
            --- a/src/App/Code.cs
            +++ b/src/App/Code.cs
            @@ -3 +3 @@
            -        var x = ValueFormatter.Format(1m);
            +        var x = ValueFormatter.FormatValue(1m);
            diff --git a/tests/Old.cs b/tests/Old.cs
            deleted file mode 100644
            index 3333333..0000000
            --- a/tests/Old.cs
            +++ /dev/null
            @@ -1 +0,0 @@
            -class Old {}
            """;

        var files = UnifiedDiff.Parse(Diff);

        Assert.Equal(2, files.Count);
        Assert.Equal(("src/App/Code.cs", false), (files[0].Path, files[0].IsDeleted));
        Assert.Equal(["        var x = ValueFormatter.FormatValue(1m);"], files[0].Added);
        Assert.Equal(("tests/Old.cs", true), (files[1].Path, files[1].IsDeleted));
    }

    [Fact]
    public void ContentLinesThatLookLikeHeadersStayContent()
    {
        // A removed SQL comment ("-- …") arrives as "--- …"; an added "++x" arrives as "+++x".
        const string Diff = """
            diff --git a/db/Migration.sql b/db/Migration.sql
            index 1111111..2222222 100644
            --- a/db/Migration.sql
            +++ b/db/Migration.sql
            @@ -1,2 +1,2 @@
            --- old comment
            +++counter;
            """;

        var file = Assert.Single(UnifiedDiff.Parse(Diff));

        Assert.Equal("db/Migration.sql", file.Path);
        Assert.Equal(["-- old comment"], file.Removed);
        Assert.Equal(["++counter;"], file.Added);
    }

    [Fact]
    public void MovedOrReindentedLinesAreNotGenuinelyNew()
    {
        var diff = new FileDiff("src/A.cs", ["    #pragma warning disable CS0618", "  new line"], ["#pragma warning disable CS0618"], false, false);

        Assert.Equal(["  new line"], diff.GenuinelyAdded);
        Assert.Empty(diff.GenuinelyRemoved);
    }
}
