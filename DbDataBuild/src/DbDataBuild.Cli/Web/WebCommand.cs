namespace DbDataBuild.Cli.Web;

/// <summary>`dbdatabuild ui web`: the read-only web interface on the loopback address. It connects to nothing itself; the commands it runs only read the project.</summary>
internal static class WebCommand
{
    public static int Run(string projectRoot, int port, bool allowApply, bool json, TextWriter output, TextWriter error, Func<string, string?> env)
    {
        if (json) { error.WriteLine("`web` is an interface and has no JSON form. Use the other commands with --format json."); return CliApp.ExitUsage; }
        if (!Directory.Exists(projectRoot)) { error.WriteLine($"The project directory `{projectRoot}` does not exist."); return CliApp.ExitUsage; }
        if (port is < 0 or > 65535) { error.WriteLine("The port must be between 1 and 65535 (0 picks a free one)."); return CliApp.ExitUsage; }
        using var server = new WebServer(projectRoot, new TuiCommand.CliHost(env), port, allowApply);
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += onCancel;
        try
        {
            var serving = server.StartAsync(stop.Token);
            output.WriteLine($"Serving {projectRoot} on this machine only. " + (allowApply ? "Plans can be applied from the page (--allow-apply): it needs the write login in this environment and a person typing the confirmation." : "Plans can be read and made, not applied (see --allow-apply)."));
            output.WriteLine($"Open {server.Address}");
            output.WriteLine("Press Ctrl+C to stop.");
            output.Flush();
            serving.Wait();
        }
        catch (System.Net.HttpListenerException ex) { error.WriteLine($"Could not listen on port {server.Port} ({ex.ErrorCode}). Try another with --port."); return CliApp.ExitUsage; }
        finally { Console.CancelKeyPress -= onCancel; }
        return CliApp.ExitOk;
    }
}
