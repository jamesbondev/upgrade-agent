using System.Text;
using UpgradeAgent.Bumping;
using UpgradeAgent.Detection;
using UpgradeAgent.Tests.TestSupport;

namespace UpgradeAgent.Tests.Bumping;

public sealed class VersionBumperTests : IDisposable
{
    private readonly TempDirectory _root = new("ua-bump-");

    [Fact]
    public void EditsCentralVersionChangingNothingElse()
    {
        const string Props = """
            <Project>
              <ItemGroup>
                <!-- keep this comment -->
                <PackageVersion Include="Foo"   Version="1.0.0" />
                <PackageVersion Include="Bar" Version="2.0.0" />
              </ItemGroup>
            </Project>
            """;
        Write("Directory.Packages.props", Props);
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Foo" /></ItemGroup></Project>""");

        var result = VersionBumper.Apply(_root.Path, [Update("Foo", "1.0.0", "1.1.0")]);

        Assert.Equal(Props.Replace("\"1.0.0\"", "\"1.1.0\"", StringComparison.Ordinal), Read("Directory.Packages.props"));
        Assert.Equal([TestData.Edit("Foo", "1.0.0", "1.1.0")], result.Edits);
        Assert.Empty(result.Manual);
    }

    [Fact]
    public void EditsPerProjectVersions()
    {
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Foo" Version="1.0.0" /></ItemGroup></Project>""");

        var result = VersionBumper.Apply(_root.Path, [Update("Foo", "1.0.0", "1.0.5")]);

        Assert.Contains("Version=\"1.0.5\"", Read("src/App/App.csproj"), StringComparison.Ordinal);
        Assert.Equal("src/App/App.csproj", Assert.Single(result.Edits).File);
    }

    [Fact]
    public void UsesTheNearestDirectoryPackagesProps()
    {
        Write("Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="Foo" Version="1.0.0" /></ItemGroup></Project>""");
        Write("src/Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="Foo" Version="1.0.0" /></ItemGroup></Project>""");
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");

        VersionBumper.Apply(_root.Path, [Update("Foo", "1.0.0", "1.1.0")]);

        Assert.Contains("1.1.0", Read("src/Directory.Packages.props"), StringComparison.Ordinal);
        Assert.Contains("1.0.0", Read("Directory.Packages.props"), StringComparison.Ordinal);
    }

    [Fact]
    public void BumpsFromAnOlderVersionWhenAnEarlierStepWasRejected()
    {
        Write("Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="Foo" Version="1.0.0" /></ItemGroup></Project>""");
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");

        var result = VersionBumper.Apply(_root.Path, [Update("Foo", "1.1.0", "2.0.0")]);

        Assert.Equal(TestData.Edit("Foo", "1.0.0", "2.0.0"), Assert.Single(result.Edits));
    }

    [Fact]
    public void PropertyVersionsAndVersionOverridesAreManual()
    {
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Foo" Version="$(FooVersion)" /><PackageReference Include="Bar" VersionOverride="1.0.0" /></ItemGroup></Project>""");

        var result = VersionBumper.Apply(_root.Path, [Update("Foo", "1.0.0", "2.0.0"), Update("Bar", "1.0.0", "2.0.0")]);

        Assert.Empty(result.Edits);
        Assert.Contains("not a plain version", result.Manual.Single(m => m.Update.Id == "Foo").Reason, StringComparison.Ordinal);
        Assert.Contains("VersionOverride", result.Manual.Single(m => m.Update.Id == "Bar").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUpdateIsNeverHalfApplied()
    {
        Write("Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="Foo" Version="1.0.0" /></ItemGroup></Project>""");
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Foo" VersionOverride="1.0.0" /></ItemGroup></Project>""");

        var result = VersionBumper.Apply(_root.Path, [Update("Foo", "1.0.0", "1.1.0")]);

        Assert.Empty(result.Edits);
        Assert.Contains("1.0.0", Read("Directory.Packages.props"), StringComparison.Ordinal);
        Assert.Contains("VersionOverride in src/App/App.csproj", Assert.Single(result.Manual).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingEntryIsManual()
    {
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");

        var result = VersionBumper.Apply(_root.Path, [Update("Foo", "1.0.0", "2.0.0")]);

        Assert.Contains("no version entry found", Assert.Single(result.Manual).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesByteOrderMark()
    {
        File.WriteAllText(_root.Combine("Directory.Packages.props"), """<Project><ItemGroup><PackageVersion Include="Foo" Version="1.0.0" /></ItemGroup></Project>""", new UTF8Encoding(true));
        Write("src/App/App.csproj", """<Project Sdk="Microsoft.NET.Sdk" />""");

        VersionBumper.Apply(_root.Path, [Update("Foo", "1.0.0", "1.1.0")]);

        var bytes = File.ReadAllBytes(_root.Combine("Directory.Packages.props"));
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
    }

    public void Dispose() => _root.Dispose();

    private static PlannedUpdate Update(string id, string from, string to) => TestData.Update(id, from, to);

    private void Write(string relativePath, string content) => _root.Write(relativePath, content);

    private string Read(string relativePath) => _root.Read(relativePath);
}
