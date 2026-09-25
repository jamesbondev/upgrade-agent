namespace AgentHarness;

public abstract record ToolRequest
{
    public abstract string Describe();
}

public sealed record ShellRequest(string CommandLine, bool WritesFile = false, IReadOnlyList<string>? PossiblePaths = null) : ToolRequest
{
    public override string Describe() => CommandLine;
}

public sealed record FileWriteRequest(string Path) : ToolRequest
{
    public override string Describe() => $"edit {Path}";
}

public sealed record FileReadRequest(string Path) : ToolRequest
{
    public override string Describe() => $"read {Path}";
}

public sealed record WebFetchRequest(string Url) : ToolRequest
{
    public override string Describe() => $"fetch {Url}";
}

public sealed record CustomToolRequest(string Name, string? Arguments = null) : ToolRequest
{
    public override string Describe() => string.IsNullOrWhiteSpace(Arguments) || Arguments == "{}" ? Name : $"{Name} {Arguments}";
}

public sealed record McpToolRequest(string Server, string Tool, string? Arguments = null, bool ReadOnly = false) : ToolRequest
{
    public override string Describe() => $"{Server}/{Tool}{(string.IsNullOrWhiteSpace(Arguments) || Arguments == "{}" ? "" : $" {Arguments}")}";
}

public sealed record OtherToolRequest(string Kind) : ToolRequest
{
    public override string Describe() => Kind;
}
