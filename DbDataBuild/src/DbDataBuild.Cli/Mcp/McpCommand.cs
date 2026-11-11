using DbDataBuild.Core;

namespace DbDataBuild.Cli.Mcp;

/// <summary>`dbdatabuild ui mcp`: the Model Context Protocol on standard input and output. It connects to nothing itself; each tool it offers is a command with its own effect class.</summary>
internal static class McpCommand
{
    public static int Run(string projectRoot, bool allowWrites, bool allowApply, bool noShow, bool json, TextReader input, TextWriter output, TextWriter error, Func<string, string?> env, string? reportTo = null, System.Net.Http.HttpMessageHandler? reportHandler = null)
    {
        if (json) { error.WriteLine("`mcp` speaks the Model Context Protocol on standard output and has no JSON form of its own."); return CliApp.ExitUsage; }
        if (!Directory.Exists(projectRoot)) { error.WriteLine($"The project directory `{projectRoot}` does not exist."); return CliApp.ExitUsage; }
        DbDataKit.Cli.ConsoleReporter? reporter = null;
        if (reportTo is not null)
        {
            // the session is recorded in a console as the person whose token this server holds (plan 27a AG29; the same reporter as every product's `ui mcp`): the address is an argument, the person and the token are never
            if (!Uri.TryCreate(reportTo, UriKind.Absolute, out var console) || console.Scheme is not ("https" or "http")) { error.WriteLine($"--report-to takes the console's address (https://...), not '{reportTo}'."); return CliApp.ExitUsage; }
            string? user = env(DbDataKit.Cli.ConsoleReporter.UserVariable), token = env(DbDataKit.Cli.ConsoleReporter.TokenVariable);
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(token))
            {
                error.WriteLine($"--report-to signs in as a person with a personal access token: set {DbDataKit.Cli.ConsoleReporter.UserVariable} (the account's name) and {DbDataKit.Cli.ConsoleReporter.TokenVariable} (a token made on the console's Agents page).");
                return CliApp.ExitUsage;
            }
            reporter = DbDataKit.Cli.ConsoleReporter.Create(console, user, token, error, reportHandler);
        }
        new McpServer(projectRoot, allowWrites, input, output, new TuiCommand.CliHost(env), allowApply, noShow)
        {
            Audit = reporter is null ? null : new DbDataKit.Cli.CompositeAudit(new DbDataKit.Cli.TextWriterAudit(error), reporter),
            Inbox = reporter is null ? null : reporter.AskInboxAsync,
        }.Serve();
        return CliApp.ExitOk;
    }
}
