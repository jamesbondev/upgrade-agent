namespace UpgradeAgent.Run;

/// <summary>The run can't continue safely (a red baseline, a config leak, a patch that no longer applies).</summary>
internal sealed class RunAbortedException(string message) : Exception(message);
