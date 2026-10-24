using DbDataKit.Cli;

namespace DbDataKit.Hosting.Cli;

/// <summary>The shared <c>service</c> and <c>cert</c> nouns over the commands in this package, so every product's command line has them under the same names (plan 13, W4).</summary>
public static class HostingNouns
{
    /// <summary><c>service install|uninstall|status|start|stop|restart|doctor</c>: registration of <c>server run</c> with Windows or systemd. Never offered through the management API: it needs elevation on the machine.</summary>
    public static Noun Service(ServiceCommand command) => new("service", "Registration of 'server run' with Windows or systemd",
    [
        Forward("install", "Register the service (again, to update it); needs elevation", EffectClass.TargetWrites, "[--account <account>] [--password-stdin] [--data-dir <path>] [--dry-run]", command.Execute),
        Forward("uninstall", "Remove the service registration; needs elevation", EffectClass.TargetWrites, "", command.Execute),
        Forward("status", "Whether the service is installed and running", EffectClass.OfflineOnly, "", command.Execute),
        Forward("start", "Start the service; needs elevation", EffectClass.TargetWrites, "", command.Execute),
        Forward("stop", "Stop the service; needs elevation", EffectClass.TargetWrites, "", command.Execute),
        Forward("restart", "Stop and start the service; needs elevation", EffectClass.TargetWrites, "", command.Execute),
        Forward("doctor", "Check the registration, the account and the directory permissions", EffectClass.OfflineOnly, "", command.Execute),
    ]);

    /// <summary><c>cert show|ensure|check|renew</c>: the server's TLS certificate. Products add their own verbs with <see cref="Noun.With"/>.</summary>
    public static Noun Cert(CertCommand command) => new("cert", "The server's TLS certificate",
    [
        Forward("show", "The certificate in use; never creates or changes anything", EffectClass.OfflineOnly, "", command.Execute),
        Forward("ensure", "Create or rotate the managed certificate if needed", EffectClass.RepoFilesOnly, "", command.Execute),
        Forward("check", "Exit 1 when the certificate is missing or due for rotation", EffectClass.OfflineOnly, "", command.Execute),
        Forward("renew", "Replace the managed self-signed certificate now", EffectClass.RepoFilesOnly, "", command.Execute),
    ]);

    private static Verb Forward(string name, string summary, EffectClass effect, string usage, Func<string[], IConsole, int> execute) =>
        new(name, summary, effect, usage, (args, context) => Task.FromResult(execute([name, .. args], new ContextConsole(context))), DataSchemas.None);

    /// <summary>A console whose output goes to the context, so that it is captured in the JSON form.</summary>
    public static IConsole ConsoleFor(CliContext context) => new ContextConsole(context);

    private sealed class ContextConsole(CliContext context) : IConsole
    {
        public TextWriter Out => context.Out;

        public TextWriter Error => context.Error;

        public string? ReadLine() => context.In.ReadLine();
    }
}
