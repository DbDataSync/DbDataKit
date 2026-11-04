using DbDataKit.Apps;
using DbDataKit.Cli;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Configuration;

namespace DbDataProxy.Server.Hosting;

/// <summary>
/// <c>dbdataproxy apps ...</c> (plan 26, AP05e): the Store servers the apps host forwards its apps' data calls to. <c>store-pair</c> pairs the host with a Store by the code its administrator made (<c>dbdatastore server pair</c>), as a console
/// is paired: the Store then knows the host's key and the ceiling it may grant there, and the host pins the Store's certificate. The pairing is kept in the data directory and is used by the running host at once.
/// </summary>
public static class AppsCommand
{
    private const string Where = "[--repo <data directory>]";

    public static Noun Noun() => new("apps", "The apps host this proxy serves: the stores it forwards app data to",
    [
        new Verb("publish", "Publish an app version from a folder: its manifest (app.yaml) and the files the host serves", EffectClass.RepoFilesOnly, $"--dir <folder> [--manifest <file, default <folder>/app.yaml>] [--note <text>] {Where}", PublishAsync,
            DataSchemas.Object([("app", DataSchemas.String), ("version", DataSchemas.Integer), ("files", DataSchemas.Integer)], "app", "version", "files")),
        new Verb("instance-save", "Make or change an instance of a published app: its version pin, title, settings and data bindings (a new instance version; the host serves it at once)", EffectClass.RepoFilesOnly,
            $"--name <instance> --app <app> [--pin <version>] [--title <text>] [--setting k=v[,k=v]] [--bind model=storeInstance[,model=storeInstance]] {Where}", InstanceSaveAsync,
            DataSchemas.Object([("name", DataSchemas.String), ("app", DataSchemas.String), ("version", DataSchemas.Integer)], "name", "app", "version")),
        new Verb("instance-list", "The instances the host serves, with the app and version each runs", EffectClass.OfflineOnly, Where, InstanceListAsync,
            DataSchemas.Object([("instances", DataSchemas.ListOf(DataSchemas.Object([("name", DataSchemas.String), ("app", DataSchemas.String), ("pin", DataSchemas.Integer), ("title", DataSchemas.String)], "name", "app")))], "instances")),
        new Verb("nav-save", "Make or change a navigation set: a titled, ordered menu the shell shows in each member instance (a person sees only the members they may open)", EffectClass.RepoFilesOnly,
            $"--name <set> --title <text> --item <instance[=label]>[,<instance[=label]>...] {Where}", NavSaveAsync,
            DataSchemas.Object([("name", DataSchemas.String), ("version", DataSchemas.Integer)], "name", "version")),
        new Verb("nav-list", "The navigation sets and their members", EffectClass.OfflineOnly, Where, NavListAsync,
            DataSchemas.Object([("sets", DataSchemas.ListOf(DataSchemas.Object([("name", DataSchemas.String), ("title", DataSchemas.String), ("instances", DataSchemas.ListOf(DataSchemas.String))], "name", "title", "instances")))], "sets")),
        new Verb("approve", "Approve one version of a trusted app, as the person who reviewed it (not its author); the approval is for that version's exact bytes and ends when a new version is published", EffectClass.RepoFilesOnly,
            $"--app <app> --version <n> --by <approver> [--note <text>] {Where}", ApproveAsync,
            DataSchemas.Object([("id", DataSchemas.String), ("app", DataSchemas.String), ("version", DataSchemas.String)], "id", "app", "version")),
        new Verb("approval-list", "The approvals on record, with who gave them and whether they still stand", EffectClass.OfflineOnly, Where, ApprovalListAsync,
            DataSchemas.Object([("approvals", DataSchemas.ListOf(DataSchemas.Object([("id", DataSchemas.String), ("app", DataSchemas.String), ("version", DataSchemas.String), ("approved_by", DataSchemas.String), ("revoked", DataSchemas.Boolean)], "id", "app", "version", "approved_by", "revoked")))], "approvals")),
        new Verb("approval-revoke", "End an approval; the app stops running as trusted. The record stays", EffectClass.RepoFilesOnly, $"--id <approval id> --by <name> {Where}", ApprovalRevokeAsync, DataSchemas.None),
        new Verb("store-pair", "Pair the apps host with a Store server, using the code its administrator made", EffectClass.RepoFilesOnly, $"--url <https://store:port> --code <pairing code> [--name <label>] {Where}", PairAsync,
            DataSchemas.Object([("id", DataSchemas.String), ("url", DataSchemas.String), ("fingerprint", DataSchemas.String)], "id", "url", "fingerprint")),
        new Verb("store-list", "The Store servers the apps host is paired with; the id is what an app's data calls name", EffectClass.OfflineOnly, Where, ListAsync,
            DataSchemas.Object([("stores", DataSchemas.ListOf(DataSchemas.Object([("id", DataSchemas.String), ("url", DataSchemas.String), ("name", DataSchemas.String), ("fingerprint", DataSchemas.String)], "id", "url", "name", "fingerprint")))], "stores")),
        new Verb("store-remove", "Stop forwarding to a Store server (unpair the host there too, with the Store's own command)", EffectClass.RepoFilesOnly, $"--id <store id> {Where}", RemoveAsync, DataSchemas.None),
    ]);

    private static ProxyOptions OptionsOf(CliArgs parsed)
    {
        var directory = parsed.Get("repo") is { Length: > 0 } repo ? Path.GetFullPath(repo) : null;
        var options = ProxyConfiguration.LoadOptions(directory);
        if (directory is not null) options.DataDirectory = directory;
        return options;
    }

    private static Dictionary<string, string>? Pairs(string? text, string what, out string? problem)
    {
        problem = null;
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.IndexOf('=');
            if (at <= 0 || at == part.Length - 1) { problem = $"'{part}' is not {what} (use name=value)."; return null; }
            found[part[..at]] = part[(at + 1)..];
        }
        return found;
    }

    private static async Task<int> PublishAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "dir", "manifest", "note", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("dir") is not { Length: > 0 } dir)
        {
            await context.Error.WriteLineAsync($"Usage: apps publish --dir <folder> [--manifest <file>] [--note <text>] {Where}");
            return CliExit.Usage;
        }
        var folder = Path.GetFullPath(dir);
        var manifestPath = parsed.Get("manifest") is { Length: > 0 } m ? Path.GetFullPath(m) : Path.Combine(folder, "app.yaml");
        if (!Directory.Exists(folder) || !File.Exists(manifestPath))
        {
            await context.Error.WriteLineAsync(!Directory.Exists(folder) ? $"{folder} is not a folder." : $"{manifestPath} does not exist: an app's manifest is app.yaml in its folder, or name it with --manifest.");
            return CliExit.Failure;
        }
        try
        {
            // the manifest is read from where it is; the files are the folder's without it
            var package = AppPackage.FromDirectory(File.ReadAllText(manifestPath), folder);
            var files = package.Files.Where(f => Path.GetFullPath(Path.Combine(folder, f.Key)) != manifestPath).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
            package = new AppPackage(package.ManifestYaml, files);
            var version = new AppInstanceStore(new DefinitionStore(IdentityCommand.Open(parsed))).PublishApp(package, Environment.UserName, note: parsed.Get("note"));
            await context.Out.WriteLineAsync($"Published {version.Name} version {version.Version} ({files.Count} files).");
            context.Data["app"] = version.Name;
            context.Data["version"] = version.Version;
            context.Data["files"] = files.Count;
            return CliExit.Ok;
        }
        catch (Exception ex) when (ex is AppInstanceException or AppPackageException)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> InstanceSaveAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "name", "app", "pin", "title", "setting", "bind", "repo" });
        var settings = Pairs(parsed.Get("setting"), "a setting", out var bad1);
        var bindings = Pairs(parsed.Get("bind"), "a binding", out var bad2);
        int? pin = null;
        if (parsed.Get("pin") is { Length: > 0 } pinText)
        {
            if (!int.TryParse(pinText, out var n) || n < 1) { await context.Error.WriteLineAsync($"'{pinText}' is not a version number."); return CliExit.Usage; }
            pin = n;
        }
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("name") is not { Length: > 0 } name || parsed.Get("app") is not { Length: > 0 } app || settings is null || bindings is null)
        {
            await context.Error.WriteLineAsync(bad1 ?? bad2 ?? $"Usage: apps instance-save --name <instance> --app <app> [--pin <version>] [--title <text>] [--setting k=v] [--bind model=instance] {Where}");
            return CliExit.Usage;
        }
        try
        {
            var saved = new AppInstanceStore(new DefinitionStore(IdentityCommand.Open(parsed))).Save(new AppInstance(name, app, pin, parsed.Get("title"), settings, bindings), Environment.UserName);
            await context.Out.WriteLineAsync($"Saved instance {name} of {app} (version {saved.Version}). It is served at /apps/{name}/.");
            context.Data["name"] = name;
            context.Data["app"] = app;
            context.Data["version"] = saved.Version;
            return CliExit.Ok;
        }
        catch (AppInstanceException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> InstanceListAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        var all = new AppInstanceStore(new DefinitionStore(IdentityCommand.Open(parsed))).List();
        foreach (var i in all) await context.Out.WriteLineAsync($"{i.Name}  {i.App}  {(i.Pin is { } p ? "pinned to " + p : "current")}{(i.Title is null ? "" : "  " + i.Title)}");
        if (all.Count == 0) await context.Out.WriteLineAsync("No instance is published. Publish an app with: apps publish --dir <folder>, then apps instance-save --name <instance> --app <app>");
        context.Data["instances"] = all.Select(i => new Dictionary<string, object?> { ["name"] = i.Name, ["app"] = i.App, ["pin"] = i.Pin, ["title"] = i.Title }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> NavSaveAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "name", "title", "item", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("name") is not { Length: > 0 } name || parsed.Get("title") is not { Length: > 0 } title || parsed.Get("item") is not { Length: > 0 } items)
        {
            await context.Error.WriteLineAsync($"Usage: apps nav-save --name <set> --title <text> --item <instance[=label]>[,...] {Where}");
            return CliExit.Usage;
        }
        var entries = items.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(part =>
        {
            var at = part.IndexOf('=');
            return at < 0 ? new NavigationEntry(part) : new NavigationEntry(part[..at], part[(at + 1)..]);
        }).ToList();
        try
        {
            var saved = new AppInstanceStore(new DefinitionStore(IdentityCommand.Open(parsed))).SaveNavigation(new NavigationSet(name, title, entries), Environment.UserName);
            await context.Out.WriteLineAsync($"Saved navigation set {name} (version {saved.Version}) with {entries.Count} instance(s).");
            context.Data["name"] = name;
            context.Data["version"] = saved.Version;
            return CliExit.Ok;
        }
        catch (AppInstanceException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> NavListAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        var sets = new AppInstanceStore(new DefinitionStore(IdentityCommand.Open(parsed))).NavigationSets();
        foreach (var set in sets) await context.Out.WriteLineAsync($"{set.Name}  {set.Title}  {string.Join(", ", set.Entries.Select(e => e.Instance))}");
        if (sets.Count == 0) await context.Out.WriteLineAsync("No navigation set is published. Make one with: apps nav-save --name <set> --title <text> --item <instance>,<instance>");
        context.Data["sets"] = sets.Select(s => new Dictionary<string, object?> { ["name"] = s.Name, ["title"] = s.Title, ["instances"] = s.Entries.Select(e => e.Instance).ToList() }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> ApproveAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "app", "version", "by", "note", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("app") is not { Length: > 0 } app || !int.TryParse(parsed.Get("version"), out var version) || parsed.Get("by") is not { Length: > 0 } by)
        {
            await context.Error.WriteLineAsync($"Usage: apps approve --app <app> --version <n> --by <approver> [--note <text>] {Where}");
            return CliExit.Usage;
        }
        try
        {
            var approval = new AppApprovalStore(new DefinitionStore(IdentityCommand.Open(parsed))).Approve(app, version, by, parsed.Get("note"));
            await context.Out.WriteLineAsync($"Approved {app} {approval.Version} ({approval.Id}) as {by}.");
            context.Data["id"] = approval.Id;
            context.Data["app"] = approval.App;
            context.Data["version"] = approval.Version;
            return CliExit.Ok;
        }
        catch (AppInstanceException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> ApprovalListAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        var all = new AppApprovalStore(new DefinitionStore(IdentityCommand.Open(parsed))).All();
        foreach (var a in all) await context.Out.WriteLineAsync($"{a.Id}  {a.App} {a.Version}  approved by {a.ApprovedBy} on {a.ApprovedAt:yyyy-MM-dd}{(a.RevokedAt is null ? "" : "  revoked")}");
        if (all.Count == 0) await context.Out.WriteLineAsync("No approval is on record. Approve a trusted app's version with: apps approve --app <app> --version <n> --by <approver>");
        context.Data["approvals"] = all.Select(a => new Dictionary<string, object?> { ["id"] = a.Id, ["app"] = a.App, ["version"] = a.Version, ["approved_by"] = a.ApprovedBy, ["revoked"] = a.RevokedAt is not null }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> ApprovalRevokeAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "id", "by", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("id") is not { Length: > 0 } id || parsed.Get("by") is not { Length: > 0 } by)
        {
            await context.Error.WriteLineAsync($"Usage: apps approval-revoke --id <approval id> --by <name> {Where}");
            return CliExit.Usage;
        }
        try
        {
            new AppApprovalStore(new DefinitionStore(IdentityCommand.Open(parsed))).Revoke(id, by);
            await context.Out.WriteLineAsync($"Revoked {id}.");
            return CliExit.Ok;
        }
        catch (AppInstanceException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> PairAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "url", "code", "name", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || !Uri.TryCreate(parsed.Get("url"), UriKind.Absolute, out var url) || parsed.Get("code") is not { Length: > 0 } code)
        {
            await context.Error.WriteLineAsync($"Usage: apps store-pair --url <https://store:port> --code <pairing code> [--name <label>] {Where}");
            return CliExit.Usage;
        }
        var options = OptionsOf(parsed);
        try
        {
            var key = await ProxyAppsModule.SigningKeyAsync(options, default);
            var paired = await PairingClient.PairAsync(url, code, new ConsoleIdentity(ProxyAppsModule.TokenIssuer, key), ClientKind.Server, "DbDataProxy apps host");
            ProxyAppsModule.StoresOf(options).Save(new AppStore(paired.InstanceId, url, paired.CertificateFingerprint, parsed.Get("name") ?? paired.Name));
            await context.Out.WriteLineAsync($"Paired with {paired.Name} ({paired.InstanceId}); the host may be given up to {string.Join(", ", ServerRoles.Names(paired.Ceiling))} there.");
            context.Data["id"] = paired.InstanceId;
            context.Data["url"] = url.AbsoluteUri;
            context.Data["fingerprint"] = paired.CertificateFingerprint;
            return CliExit.Ok;
        }
        catch (PairingFailedException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> ListAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        var stores = ProxyAppsModule.StoresOf(OptionsOf(parsed)).All();
        foreach (var s in stores) await context.Out.WriteLineAsync($"{s.Id}  {s.Url}  {s.Name}  {s.Fingerprint}");
        if (stores.Count == 0) await context.Out.WriteLineAsync("No Store server is paired. Pair one with: apps store-pair --url <https://store:port> --code <pairing code>");
        context.Data["stores"] = stores.Select(s => new Dictionary<string, object?> { ["id"] = s.Id, ["url"] = s.Url.AbsoluteUri, ["name"] = s.Name, ["fingerprint"] = s.Fingerprint }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> RemoveAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "id", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("id") is not { Length: > 0 } id)
        {
            await context.Error.WriteLineAsync($"Usage: apps store-remove --id <store id> {Where}");
            return CliExit.Usage;
        }
        if (!ProxyAppsModule.StoresOf(OptionsOf(parsed)).Remove(id))
        {
            await context.Error.WriteLineAsync($"No paired Store server has the id {id}.");
            return CliExit.Failure;
        }
        await context.Out.WriteLineAsync($"No longer forwarding to {id}.");
        return CliExit.Ok;
    }
}
