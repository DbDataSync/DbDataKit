using DbDataKit.Cli;

namespace DbDataKit.Web.Trust;

/// <summary>Where a server keeps what pairing needs; the product says, the verbs do the rest.</summary>
/// <param name="ServerYaml">The server's <c>server.yaml</c>; the pairing codes wait in <c>pairing-codes.json</c> beside it.</param>
public sealed record PairingFiles(string ServerYaml)
{
    public PairedClientsFile Clients() => new(ServerYaml);
    public PairingCodes Codes() => new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ServerYaml))!, "pairing-codes.json"));
}

/// <summary>
/// The <c>server pair</c>, <c>server clients</c> and <c>server unpair</c> verbs (plan 13, W12), the same in every product: a product adds them to its <c>server</c> noun with
/// <see cref="Noun.With"/> and says where its <c>server.yaml</c> is. Running them needs the rights to write that file, which is the proof that the person owns the server.
/// </summary>
public static class PairingVerbs
{
    public static Verb[] Create(Func<string[], PairingFiles> files) =>
    [
        new Verb("pair", "Make a single-use code that pairs a console, another server or a command line with this server", EffectClass.RepoFilesOnly,
            "[--max-role <role>[,<role>...]] [--name <label>] [--minutes <n>] [--repo <path>]",
            (args, context) => Task.FromResult(Pair(args, context, files)),
            DataSchemas.Object([("code", DataSchemas.String), ("expires", DataSchemas.String), ("max_role", DataSchemas.StringList)], "code", "expires", "max_role")),
        new Verb("clients", "The paired clients: kind, name, id, key fingerprint, ceiling, when paired", EffectClass.OfflineOnly, "[--repo <path>]",
            (args, context) => Task.FromResult(Clients(args, context, files)),
            DataSchemas.Object([("clients", DataSchemas.ListOf(DataSchemas.Object(
                [("kind", DataSchemas.String), ("name", DataSchemas.String), ("id", DataSchemas.String), ("fingerprint", DataSchemas.String), ("ceiling", DataSchemas.StringList), ("paired", DataSchemas.String)],
                "kind", "name", "id", "fingerprint", "ceiling")))], "clients")),
        new Verb("unpair", "Remove a pairing, effective on the client's next call", EffectClass.RepoFilesOnly, "<client> [--repo <path>]",
            (args, context) => Task.FromResult(Unpair(args, context, files)),
            DataSchemas.Object([("unpaired", DataSchemas.String)], "unpaired")),
    ];

    private static readonly IReadOnlySet<string> NoFlags = new HashSet<string>();

    private static int Pair(string[] args, CliContext context, Func<string[], PairingFiles> files)
    {
        var parsed = CliArgs.Parse(args, NoFlags, new HashSet<string> { "max-role", "name", "minutes", "repo" });
        if (Bad(parsed, context) is { } bad) return bad;
        var ceiling = ServerRole.Admin;
        if (parsed.Get("max-role") is { } roles && (!ServerRoles.TryParse(roles.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), out ceiling) || ceiling == ServerRole.None))
        {
            context.Error.WriteLine($"'{roles}' is not a list of roles (viewer, operator, author, approver, admin, publisher).");
            return CliExit.Usage;
        }
        var lifetime = PairingCodes.DefaultLifetime;
        if (parsed.Get("minutes") is { } minutes)
        {
            if (!int.TryParse(minutes, out var m) || m is < 1 or > 1440) { context.Error.WriteLine("--minutes is a whole number from 1 to 1440."); return CliExit.Usage; }
            lifetime = TimeSpan.FromMinutes(m);
        }
        var code = files(args).Codes().Create(ServerRoles.Close(ceiling), parsed.Get("name"), lifetime);
        var names = ServerRoles.Names(ServerRoles.Close(ceiling));
        context.Out.WriteLine($"Pairing code: {code.Code}");
        context.Out.WriteLine($"It works once, until {code.ExpiresAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC, and lets the client grant at most: {string.Join(", ", names)}.");
        context.Data["code"] = code.Code;
        context.Data["expires"] = code.ExpiresAt.UtcDateTime.ToString("O");
        context.Data["max_role"] = names;
        return CliExit.Ok;
    }

    private static int Clients(string[] args, CliContext context, Func<string[], PairingFiles> files)
    {
        var parsed = CliArgs.Parse(args, NoFlags, new HashSet<string> { "repo" });
        if (Bad(parsed, context) is { } bad) return bad;
        var file = files(args).Clients();
        var all = file.All();
        foreach (var problem in file.Problems) context.Error.WriteLine(problem);
        if (all.Count == 0) context.Out.WriteLine("No client is paired. Make a code with 'server pair'.");
        foreach (var c in all)
            context.Out.WriteLine($"{c.Kind.ToString().ToLowerInvariant(),-8} {c.Name}  id {c.Id}  key {Fingerprints.Of(c.PublicKey)}  may grant {string.Join(",", ServerRoles.Names(c.Ceiling))}  paired {c.PairedAt?.UtcDateTime:yyyy-MM-dd}");
        context.Data["clients"] = all.Select(c => new Dictionary<string, object?>
        {
            ["kind"] = c.Kind.ToString().ToLowerInvariant(), ["name"] = c.Name, ["id"] = c.Id, ["fingerprint"] = Fingerprints.Of(c.PublicKey),
            ["ceiling"] = ServerRoles.Names(c.Ceiling), ["paired"] = c.PairedAt?.UtcDateTime.ToString("O"),
        }).ToList();
        return CliExit.Ok;
    }

    private static int Unpair(string[] args, CliContext context, Func<string[], PairingFiles> files)
    {
        var parsed = CliArgs.Parse(args, NoFlags, new HashSet<string> { "repo" });
        if (Bad(parsed, context) is { } bad) return bad;
        if (parsed.Words.Count != 1) { context.Error.WriteLine("Usage: unpair <client id or name>"); return CliExit.Usage; }
        var file = files(args).Clients();
        var wanted = parsed.Words[0];
        var matches = file.All().Where(c => c.Id == wanted).ToList();
        if (matches.Count == 0) matches = file.All().Where(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) { context.Error.WriteLine($"No paired client is called '{wanted}'. 'server clients' lists them."); return CliExit.Failure; }
        if (matches.Count > 1) { context.Error.WriteLine($"{matches.Count} clients are called '{wanted}'; name one by id: {string.Join(", ", matches.Select(c => c.Id))}."); return CliExit.Failure; }
        file.Remove(matches[0].Id);
        context.Out.WriteLine($"Unpaired {matches[0].Name} ({matches[0].Id}). Its next call is refused.");
        context.Data["unpaired"] = matches[0].Id;
        return CliExit.Ok;
    }

    private static int? Bad(CliArgs parsed, CliContext context)
    {
        if (parsed.Unknown.Count == 0) return null;
        context.Error.WriteLine("Unknown or incomplete option: " + string.Join(", ", parsed.Unknown));
        return CliExit.Usage;
    }
}
