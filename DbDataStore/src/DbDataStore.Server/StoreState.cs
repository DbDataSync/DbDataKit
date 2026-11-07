using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataStore.Server;

/// <summary>
/// The DbDataStore server's state database (plan 29a Q18): <c>state.db</c> in the server's directory, built from the <c>trust</c> module. The server's instance id, the clients paired with it and the
/// pairing codes waiting are kept there, not in <c>server.yaml</c>. Pairings made before this were a list in <c>server.yaml</c>; they are not carried over (a clean break, plan 13 section 3): a server
/// whose file still lists them does not start, and says which file and what to do.
/// </summary>
public static class StoreState
{
    public const string FileName = "state.db";

    public const string ServiceId = "dbdatastore";

    public static StateScope Scope { get; } = new(Group: "main", Service: ServiceId);

    /// <summary>The modules the server's state is built from.</summary>
    public static IReadOnlyList<StateModule> Modules { get; } = [TrustModule.Module];

    public static StateDatabase Open(string directory)
    {
        var yaml = Path.Combine(directory, "server.yaml");
        if (File.Exists(yaml) && File.ReadLines(yaml).Any(line => line.StartsWith(PairedClientsFile.Key + ":", StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"{yaml} lists paired clients ({PairedClientsFile.Key}:), which a DbDataStore server now keeps in its state database ({Path.Combine(directory, FileName)}). " +
                $"Pairings are not converted: remove the {PairedClientsFile.Key} list from {yaml}, then pair each client again with 'dbdatastore server pair'.");
        Directory.CreateDirectory(directory);
        return StateDatabase.OpenModules(Path.Combine(directory, FileName), Modules, Scope);
    }

    /// <summary>Where the server keeps what pairing needs, for the server and for the <c>server pair</c>, <c>clients</c> and <c>unpair</c> verbs.</summary>
    public static PairingFiles Pairing(string directory) => new(Path.Combine(directory, "server.yaml"), Group: Open(directory));
}
