using System.CommandLine;
using TestHardener.Cli;

var root = new RootCommand("TestHardener: runs Stryker.NET on the repos you list and finds the mutants the tests don't catch.")
{
    CommonOptions.Config,
    CommonOptions.Verbose,
    SurveyCommand.Create(),
};

return await root.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = TimeSpan.FromSeconds(30) });
