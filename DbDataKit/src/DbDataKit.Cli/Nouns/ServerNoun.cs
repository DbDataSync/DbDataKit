namespace DbDataKit.Cli;

/// <summary>
/// <c>server run</c>: the long-running process, in the foreground. It is what the operating system's service runs (<c>service install</c> registers it), so it never
/// detaches and stops when its cancellation token does. The product supplies how its server starts; the pairing verbs (<c>pair</c>, <c>unpair</c>, <c>consoles</c>) are added with
/// <see cref="Noun.With"/> where a product has them.
/// </summary>
public static class ServerNoun
{
    /// <param name="run">Starts the product's server with the arguments after <c>server run</c> and returns its exit code when it has stopped.</param>
    public static Noun Create(Func<string[], CliContext, Task<int>> run) => new("server", "The long-running process",
    [
        new Verb("run", "Run the server in the foreground (what the operating system's service runs); not offered through the management API", EffectClass.OfflineOnly, "[options]", run),
    ]);
}
