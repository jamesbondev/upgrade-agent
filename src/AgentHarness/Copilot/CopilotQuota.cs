namespace AgentHarness.Copilot;

public static class CopilotQuota
{
    public static bool IsExceeded(string? message) =>
        message is not null
        && (message.Contains("exceeded your monthly quota", StringComparison.OrdinalIgnoreCase)
            || message.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase));
}
