using AgentHarness.Testing;

namespace HelloAgent;

internal static class ScriptedDemo
{
    private const string Before = """
        namespace Demo;

        public static class Greeter
        {
            // TODO: greet by name
            public static string Greet() => "Hello!";
        }
        """;

    private const string After = """
        namespace Demo;

        public static class Greeter
        {
            public static string Greet(string name) => $"Hello, {name}!";
        }
        """;

    public static string CreateWorkspace()
    {
        var folder = Directory.CreateTempSubdirectory("hello-agent-").FullName;
        Directory.CreateDirectory(Path.Combine(folder, "src"));
        File.WriteAllText(Path.Combine(folder, "src", "Greeter.cs"), Before);
        File.WriteAllText(Path.Combine(folder, "README.md"), "# Demo\n");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Directory.Delete(folder, recursive: true);
        return folder;
    }

    public static ScriptedBackend Backend() => new ScriptedBackend()
        .Turn(t => t
            .Usage(inputTokens: 2400, outputTokens: 180)
            .Say("Let me look at the code first.")
            .Shell("ls src", output: "Greeter.cs")
            .Read("src/Greeter.cs")
            .CallTool("list_todos")
            .Shell("curl -s https://example.com/style-guide")
            .Edit("src/Greeter.cs", After)
            .Edit("README.md", "# Demo\n\nGreeter.Greet(name) greets by name.\n")
            .Shell("dotnet build", output: "Build succeeded.")
            .Reply("Greeter.Greet now takes a name, and the build passes."))
        .Turn(t => t
            .Usage(inputTokens: 2600, outputTokens: 60)
            .ReplyJson(new Summary { Outcome = "Greet takes a name; the build passes.", FilesChanged = ["src/Greeter.cs"] }));
}
