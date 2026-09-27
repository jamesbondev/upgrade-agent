using AgentHarness.Copilot;
using GitHub.Copilot;
using Microsoft.Extensions.AI;

namespace AgentHarness.Tests;

public class CopilotBackendTests
{
    [Fact]
    public async Task GateApprovalRequiredTools_AsksOnlyForApprovalRequiredTools()
    {
        var config = new SessionConfig
        {
            Tools =
            [
                new ApprovalRequiredAIFunction(AIFunctionFactory.Create(() => "x", "deploy")),
                AIFunctionFactory.Create(() => "y", "lookup"),
            ],
        };

        CopilotBackend.GateApprovalRequiredTools(config);

        var gated = await config.Hooks!.OnPreToolUse!(new PreToolUseHookInput { ToolName = "deploy" }, null!);
        var free = await config.Hooks.OnPreToolUse(new PreToolUseHookInput { ToolName = "lookup" }, null!);
        Assert.Equal("ask", gated!.PermissionDecision);
        Assert.Null(free);
    }

    [Fact]
    public void GateApprovalRequiredTools_LeavesConfigWithoutGatedToolsAlone()
    {
        var config = new SessionConfig { Tools = [AIFunctionFactory.Create(() => "y", "lookup")] };

        CopilotBackend.GateApprovalRequiredTools(config);

        Assert.Null(config.Hooks);
    }
}
