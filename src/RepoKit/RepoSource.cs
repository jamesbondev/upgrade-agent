namespace RepoKit;

public sealed class RepoSource
{
    private RepoSource(string name, string location, bool isRemote, string? authorizationHeader)
    {
        Name = name;
        Location = location;
        IsRemote = isRemote;
        AuthorizationHeader = authorizationHeader;
    }

    public string Name { get; }

    public string Location { get; }

    public bool IsRemote { get; }

    internal string? AuthorizationHeader { get; }

    public static RepoSource Remote(string name, string url, string? authorizationHeader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"'{url}' is not an absolute URL.", nameof(url));
        }

        if (uri.UserInfo.Length > 0)
        {
            throw new ArgumentException("Pass credentials as an authorization header, not in the URL.", nameof(url));
        }

        return new RepoSource(name, url, isRemote: true, authorizationHeader);
    }

    public static RepoSource Local(string name, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new RepoSource(name, Path.GetFullPath(path), isRemote: false, authorizationHeader: null);
    }

    public override string ToString() => $"{Name} ({Location})";
}
