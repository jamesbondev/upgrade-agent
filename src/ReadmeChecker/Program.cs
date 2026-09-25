using System.CommandLine;
using ReadmeChecker.Cli;

var root = new RootCommand("ReadmeChecker: finds READMEs that no longer match their repos, across the repos you list, and opens draft pull requests to fix them.")
{
    CommonOptions.Config,
    CommonOptions.Verbose,
    CheckCommand.Create(),
    FixCommand.Create(),
};

return await root.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = TimeSpan.FromSeconds(30) });
