using TestHardener.Analysis;
using TestHardener.Config;
using TestHardener.Hardening;
using TestHardener.Stryker;

namespace TestHardener.Tests.Hardening;

public class OwnedFileChooserTests
{
    private static readonly TargetConfig Target = new(
        "Engine", "src/Engine/Engine.csproj", ["tests/Engine.Tests/Engine.Tests.csproj"], null, [], []);

    [Fact]
    public void Choose_PrefersTheFileHoldingTheCoveringTests()
    {
        var group = Group("src/Engine/Reviews/Lenses/StandardLens.cs", "StandardLens", coveringFiles: ["tests/Engine.Tests/Reviews/Lenses/AllLensTests.cs"]);
        string[] tracked = ["tests/Engine.Tests/Reviews/Lenses/AllLensTests.cs", "tests/Engine.Tests/Reviews/Lenses/StandardLensTests.cs"];

        var owned = OwnedFileChooser.Choose(group, Target, tracked, _ => "")!;

        Assert.Equal("tests/Engine.Tests/Reviews/Lenses/AllLensTests.cs", owned.Path);
        Assert.Equal("tests/Engine.Tests/Engine.Tests.csproj", owned.TestProject);
        Assert.False(owned.IsNew);
    }

    [Fact]
    public void Choose_IgnoresCoveringFilesOutsideTheTargetTestProjects()
    {
        var group = Group("src/Engine/Prompts/PromptBuilder.cs", "PromptBuilder", coveringFiles: ["tests/Integration.Tests/PipelineTests.cs"]);
        string[] tracked = ["tests/Integration.Tests/PipelineTests.cs", "tests/Engine.Tests/Prompts/PromptBuilderLensTests.cs"];

        var owned = OwnedFileChooser.Choose(group, Target, tracked, f => f.EndsWith("PromptBuilderLensTests.cs", StringComparison.Ordinal) ? "new PromptBuilder()" : null)!;

        Assert.Equal("tests/Engine.Tests/Prompts/PromptBuilderLensTests.cs", owned.Path);
    }

    [Fact]
    public void Choose_ReferencingFile_PrefersTheMirroredFolder()
    {
        var group = Group("src/Engine/Prompts/PromptBuilder.cs", "PromptBuilder");
        string[] tracked = ["tests/Engine.Tests/Other/UsesPromptBuilderTests.cs", "tests/Engine.Tests/Prompts/PromptBuilderJudgeTests.cs"];

        var owned = OwnedFileChooser.Choose(group, Target, tracked, _ => "PromptBuilder PromptBuilder")!;

        Assert.Equal("tests/Engine.Tests/Prompts/PromptBuilderJudgeTests.cs", owned.Path);
    }

    [Fact]
    public void Choose_WholeWordReferencesOnly()
    {
        var group = Group("src/Engine/Prompts/Prompt.cs", "Prompt");
        string[] tracked = ["tests/Engine.Tests/Prompts/PromptBuilderTests.cs"];

        var owned = OwnedFileChooser.Choose(group, Target, tracked, _ => "new PromptBuilder()")!;

        Assert.True(owned.IsNew);
        Assert.Equal("tests/Engine.Tests/Prompts/PromptTests.cs", owned.Path);
    }

    [Fact]
    public void Skeleton_UsesRootNamespaceFolderAndSourceNamespace()
    {
        var owned = new OwnedFile("tests/Engine.Tests/Reviews/Lenses/StandardLensTests.cs", "tests/Engine.Tests/Engine.Tests.csproj", true, "new");
        const string project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><RootNamespace>ClearCoder.Engine.Tests</RootNamespace></PropertyGroup></Project>";
        const string source = "namespace ClearCoder.Engine.Reviews.Lenses;\n\ninternal sealed class StandardLens { }";

        var skeleton = OwnedFileChooser.Skeleton(owned, project, source, "StandardLens");

        Assert.Equal(
            "using ClearCoder.Engine.Reviews.Lenses;\n\nnamespace ClearCoder.Engine.Tests.Reviews.Lenses;\n\npublic class StandardLensTests\n{\n}\n",
            skeleton);
    }

    [Fact]
    public void Skeleton_WithoutRootNamespace_UsesTheProjectName()
    {
        var owned = new OwnedFile("tests/Engine.Tests/StandardLensTests.cs", "tests/Engine.Tests/Engine.Tests.csproj", true, "new");

        var skeleton = OwnedFileChooser.Skeleton(owned, "<!DOCTYPE x [<!ENTITY a \"b\">]><Project />", null, "StandardLens");

        Assert.Equal("namespace Engine.Tests;\n\npublic class StandardLensTests\n{\n}\n", skeleton);
    }

    private static SurvivorGroup Group(string file, string type, IReadOnlyList<string>? coveringFiles = null) => new()
    {
        File = file,
        Member = new MemberInfo($"{type}.Run()", "method", 1, 5, 0, 10, false, type),
        Survivors = [new Survivor("1", "Equality mutation", new Location(new(2, 1), new(2, 5)), "a > b", "a >= b", false, ["T.A"])],
        CoveringTests = ["T.A"],
        CoveringTestFiles = coveringFiles ?? [],
    };
}
