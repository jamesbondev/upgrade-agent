using System.CommandLine;
using UpgradeAgent.Cli;

var root = new RootCommand("UpgradeAgent: keeps a .NET repo's NuGet packages up to date, with an AI agent fixing breaking changes.")
{
    CommonOptions.Config,
    CommonOptions.Verbose,
    PlanCommand.Create(),
    RunCommand.Create(),
    AzureDevOpsCommands.Cleanup(),
    AzureDevOpsCommands.Seed(),
};

// Ctrl+C cancels the run; the current group is then reverted and the report written. The default
// 2-second grace period is too short for that, so allow 30 seconds before the process is killed.
return await root.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = TimeSpan.FromSeconds(30) });
