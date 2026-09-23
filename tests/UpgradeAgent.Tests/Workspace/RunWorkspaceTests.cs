using UpgradeAgent.Workspace;

namespace UpgradeAgent.Tests.Workspace;

public sealed class RunWorkspaceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ua-ws-").FullName;

    [Fact]
    public void SiblingWorktreeInheritsNothingNew()
    {
        Directory.CreateDirectory(Path.Combine(_root, "repo"));
        Directory.CreateDirectory(Path.Combine(_root, ".ua-work", "run1"));
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");

        Assert.Empty(RunWorkspace.FindConfigLeaks(Path.Combine(_root, "repo"), Path.Combine(_root, ".ua-work", "run1")));
    }

    [Fact]
    public void WorktreeUnderAnotherRepoLeaksItsConfig()
    {
        Directory.CreateDirectory(Path.Combine(_root, "repo"));
        Directory.CreateDirectory(Path.Combine(_root, "tool", "work", "run1"));
        File.WriteAllText(Path.Combine(_root, "tool", "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "tool", "nuget.config"), "<configuration />");

        var leaks = RunWorkspace.FindConfigLeaks(Path.Combine(_root, "repo"), Path.Combine(_root, "tool", "work", "run1"));

        Assert.Equal(["Directory.Build.props", "nuget.config"], leaks.Select(Path.GetFileName).Order());
    }

    [Fact]
    public void DefaultWorkRootIsBesideTheRepo()
    {
        Assert.Equal(Path.Combine(_root, ".ua-work"), RunWorkspace.DefaultWorkRoot(Path.Combine(_root, "repo") + Path.DirectorySeparatorChar));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
