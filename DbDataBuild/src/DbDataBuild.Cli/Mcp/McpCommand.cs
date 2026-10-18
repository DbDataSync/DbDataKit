using DbDataBuild.Core;

namespace DbDataBuild.Cli.Mcp;

/// <summary>`dbdatabuild ui mcp`: the Model Context Protocol on standard input and output. It connects to nothing itself; each tool it offers is a command with its own effect class.</summary>
internal static class McpCommand
{
    public static int Run(string projectRoot, bool allowWrites, bool allowApply, bool noShow, bool json, TextReader input, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        if (json) { error.WriteLine("`mcp` speaks the Model Context Protocol on standard output and has no JSON form of its own."); return CliApp.ExitUsage; }
        if (!Directory.Exists(projectRoot)) { error.WriteLine($"The project directory `{projectRoot}` does not exist."); return CliApp.ExitUsage; }
        new McpServer(projectRoot, allowWrites, input, output, new TuiCommand.CliHost(env), allowApply, noShow).Serve();
        return CliApp.ExitOk;
    }
}
