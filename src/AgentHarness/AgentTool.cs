using Microsoft.Extensions.AI;

namespace AgentHarness;

/// <summary>
/// One of your own functions, offered to the agent as a tool. Parameters and the return value are described to
/// the model from the method's signature and <see cref="System.ComponentModel.DescriptionAttribute"/>s.
/// <code>
/// var lookup = AgentTool.Create(async (string id, CancellationToken ct) => await tickets.GetAsync(id, ct),
///     "get_ticket", "Returns the ticket's title and description.");
/// var deploy = AgentTool.Create(DeployAsync, "deploy", "Deploys the branch.", requiresApproval: true);
/// </code>
/// </summary>
public sealed class AgentTool
{
    private AgentTool(AIFunction function, bool requiresApproval)
    {
        Function = function;
        RequiresApproval = requiresApproval;
    }

    public string Name => Function.Name;

    public AIFunction Function { get; }

    /// <summary>
    /// When true, every call is first decided by the session's policy as a <see cref="CustomToolRequest"/>
    /// (<see cref="Policies.WorkspacePolicy"/> asks the operator). Use it for anything with side effects outside
    /// the working folder. When false, calls run without asking.
    /// </summary>
    public bool RequiresApproval { get; }

    /// <param name="name">snake_case reads best to models. Defaults to the method's name.</param>
    public static AgentTool Create(Delegate method, string? name = null, string? description = null, bool requiresApproval = false) =>
        new(AIFunctionFactory.Create(method, name, description), requiresApproval);

    /// <summary>Wraps a function you already have (from another library, or an MCP client).</summary>
    public static AgentTool From(AIFunction function, bool requiresApproval = false) => new(function, requiresApproval);
}
