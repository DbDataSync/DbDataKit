using DbDataKit.Cli;

namespace DbDataKit.Secrets.Cli;

/// <summary>
/// The shared <c>secret</c> noun over <see cref="SecretCli"/>. A secret is read from a hidden prompt or standard input, so the console is the product's own (<see cref="SystemConsole"/>
/// for a terminal) and not the plain writers of a <see cref="CliContext"/>; no verb prints a value.
/// </summary>
public static class SecretNoun
{
    public static Noun Create(SecretCli cli, Func<IConsole> console) => new("secret", "Secret references and protected values",
    [
        Forward(cli, console, "set", "Store one secret; the value comes from a prompt or standard input, never an argument", EffectClass.RepoFilesOnly, "<name> [--stdin]"),
        Forward(cli, console, "list", "Names and when each was written, never values", EffectClass.OfflineOnly, "[<prefix>]"),
        Forward(cli, console, "delete", "Remove one secret", EffectClass.RepoFilesOnly, "<name>"),
        Forward(cli, console, "check", "Whether each reference resolves (every one in the configuration when none is named)", EffectClass.OfflineOnly, "[<reference> ...]"),
        Forward(cli, console, "rotate-key", "Re-encrypt every secret under a new key", EffectClass.RepoFilesOnly, ""),
        Forward(cli, console, "export", "Write every secret to a file under a passphrase", EffectClass.OfflineOnly, "<file> [--prefix <prefix>] [--passphrase-stdin]"),
        Forward(cli, console, "import", "Read secrets from an export file", EffectClass.RepoFilesOnly, "<file> [--overwrite] [--passphrase-stdin]"),
        Forward(cli, console, "protect", "An inline, encrypted form of one value for a single field", EffectClass.OfflineOnly, "[--stdin]"),
    ]);

    private static Verb Forward(SecretCli cli, Func<IConsole> console, string name, string summary, EffectClass effect, string usage) =>
        new(name, summary, effect, usage, (args, context) => cli.ExecuteAsync([name, .. args], console(), context.Cancellation));
}
