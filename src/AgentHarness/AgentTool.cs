using Microsoft.Extensions.AI;

namespace AgentHarness;

public sealed class AgentTool
{
    private AgentTool(AIFunction function, bool requiresApproval)
    {
        Function = function;
        RequiresApproval = requiresApproval;
    }

    public string Name => Function.Name;

    public AIFunction Function { get; }

    public bool RequiresApproval { get; }

    public static AgentTool Create(Delegate method, string? name = null, string? description = null, bool requiresApproval = false) =>
        new(AIFunctionFactory.Create(method, name, description), requiresApproval);

    public static AgentTool From(AIFunction function, bool requiresApproval = false) => new(function, requiresApproval);
}
