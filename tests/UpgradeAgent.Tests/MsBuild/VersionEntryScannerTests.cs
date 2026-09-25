using UpgradeAgent.MsBuild;

namespace UpgradeAgent.Tests.MsBuild;

public class VersionEntryScannerTests
{
    [Fact]
    public void FindsCentralVersionsWithValueOffsets()
    {
        const string Text = """
            <Project>
              <ItemGroup>
                <PackageVersion Include="Foo" Version="1.0.0" />
                <PackageVersion Include='Bar' Version='2.3.4' />
              </ItemGroup>
            </Project>
            """;

        var entries = VersionEntryScanner.Scan(Text);

        Assert.Equal(["Foo", "Bar"], entries.Select(e => e.Id));
        Assert.All(entries, e => Assert.Equal(e.Value!.Text, Text.Substring(e.Value.Start, e.Value.Length)));
    }

    [Fact]
    public void FindsChildVersionElementsAndMultilineTags()
    {
        const string Text = """
            <ItemGroup>
              <PackageReference
                  Include="Foo"
                  PrivateAssets="all">
                <Version>1.2.3</Version>
              </PackageReference>
            </ItemGroup>
            """;

        var entry = Assert.Single(VersionEntryScanner.Scan(Text));

        Assert.Equal(("Foo", "1.2.3"), (entry.Id, entry.Value!.Text));
        Assert.Equal("1.2.3", Text.Substring(entry.Value.Start, entry.Value.Length));
    }

    [Fact]
    public void IgnoresEntriesInComments()
    {
        const string Text = """
            <!-- <PackageVersion Include="Old" Version="0.1.0" /> -->
            <PackageVersion Include="Foo" Version="1.0.0" />
            """;

        Assert.Equal(["Foo"], VersionEntryScanner.Scan(Text).Select(e => e.Id));
    }

    [Fact]
    public void AttributesMayContainAngleBrackets()
    {
        const string Text = """<PackageReference Include="Foo" Version="1.0.0" Condition="'$(Major)' > '1'" />""";

        Assert.Equal("1.0.0", Assert.Single(VersionEntryScanner.Scan(Text)).Value!.Text);
    }

    [Fact]
    public void ReportsVersionOverrideAndCentralReferencesWithoutVersion()
    {
        const string Text = """
            <PackageReference Include="Foo" />
            <PackageReference Include="Bar" VersionOverride="3.0.0" />
            <PackageReference Update="Baz" Version="$(BazVersion)" />
            <GlobalPackageReference Include="Analyzers" Version="9.0.0" />
            """;

        var entries = VersionEntryScanner.Scan(Text);

        Assert.Null(entries[0].Value);
        Assert.True(entries[1].HasVersionOverride);
        Assert.Null(entries[1].Value);
        Assert.Equal(("Baz", "$(BazVersion)"), (entries[2].Id, entries[2].Value!.Text));
        Assert.Equal(("GlobalPackageReference", "9.0.0"), (entries[3].Element, entries[3].Value!.Text));
    }
}
