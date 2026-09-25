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

return await root.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = TimeSpan.FromSeconds(30) });
