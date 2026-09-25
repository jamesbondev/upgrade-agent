namespace AgentHarness.Tests.TestSupport;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix = "agent-harness-") => Path = Directory.CreateTempSubdirectory(prefix).FullName;

    public string Path { get; }

    public string Combine(string relativePath) => System.IO.Path.Combine(Path, relativePath);

    public TempDirectory Write(string relativePath, string content)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    public bool Exists(string relativePath) => File.Exists(Combine(relativePath));

    public string Read(string relativePath) => File.ReadAllText(Combine(relativePath));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
