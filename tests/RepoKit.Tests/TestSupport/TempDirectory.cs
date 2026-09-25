namespace RepoKit.Tests.TestSupport;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory(string prefix = "rk-test-") => Path = Directory.CreateTempSubdirectory(prefix).FullName;

    public string Path { get; }

    public string Combine(string relativePath) => System.IO.Path.Combine(Path, relativePath);

    public TempDirectory Write(string relativePath, string content)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    public string Read(string relativePath) => File.ReadAllText(Combine(relativePath));

    public void Delete(string relativePath) => File.Delete(Combine(relativePath));

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
