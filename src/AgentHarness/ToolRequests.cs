namespace AgentHarness;

/// <summary>An action the agent wants to take, described without SDK types. Policies decide on these.</summary>
public abstract record ToolRequest
{
    /// <summary>A one-line description for logs and approval prompts, e.g. "edit src/Foo.cs".</summary>
    public abstract string Describe();
}

/// <param name="WritesFile">The runtime saw a redirection to a file (<c>&gt; out.txt</c>).</param>
/// <param name="PossiblePaths">Paths the runtime's own parser found in the command, if it reports them.</param>
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

/// <summary>A call to one of the app's own tools (<see cref="AgentTool"/>) that needs approval.</summary>
/// <param name="Arguments">The call's arguments as JSON, as the model sent them.</param>
public sealed record CustomToolRequest(string Name, string? Arguments = null) : ToolRequest
{
    public override string Describe() => string.IsNullOrWhiteSpace(Arguments) || Arguments == "{}" ? Name : $"{Name} {Arguments}";
}

/// <summary>A call to a tool on an MCP server (configured through the backend, e.g. <c>CopilotOptions.ConfigureSession</c>).</summary>
/// <param name="ReadOnly">The server says the tool only reads.</param>
public sealed record McpToolRequest(string Server, string Tool, string? Arguments = null, bool ReadOnly = false) : ToolRequest
{
    public override string Describe() => $"{Server}/{Tool}{(string.IsNullOrWhiteSpace(Arguments) || Arguments == "{}" ? "" : $" {Arguments}")}";
}

/// <summary>Anything else the runtime offers (memory, extensions…). Refused unless a policy says otherwise.</summary>
public sealed record OtherToolRequest(string Kind) : ToolRequest
{
    public override string Describe() => Kind;
}
