using DbDataKit.Apps;
using DbDataKit.Cli;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Configuration;

namespace DbDataProxy.Server.Hosting;

/// <summary>
/// <c>dbdataproxy apps ...</c> (plan 26, AP05f): the apps, instances and navigation sets the apps host serves, in the Proxy's state. An app's data is the models published to the group (plan 20 O08c); the <c>store-*</c>
/// verbs that paired the host with a Store server are gone and say so.
/// </summary>
public static class AppsCommand
{
    private const string Where = "[--repo <data directory>]";

    public static Noun Noun() => new("apps", "The apps host this proxy serves: its apps, instances and navigation sets",
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
            $"--app <app> --version <n> --by <approver> [--egress <directive>=<origin>[,...]] [--note <text>] {Where}", ApproveAsync,
            DataSchemas.Object([("id", DataSchemas.String), ("app", DataSchemas.String), ("version", DataSchemas.String)], "id", "app", "version")),
        new Verb("approval-list", "The approvals on record, with who gave them and whether they still stand", EffectClass.OfflineOnly, Where, ApprovalListAsync,
            DataSchemas.Object([("approvals", DataSchemas.ListOf(DataSchemas.Object([("id", DataSchemas.String), ("app", DataSchemas.String), ("version", DataSchemas.String), ("approved_by", DataSchemas.String), ("revoked", DataSchemas.Boolean)], "id", "app", "version", "approved_by", "revoked")))], "approvals")),
        new Verb("approval-revoke", "End an approval; the app stops running as trusted. The record stays", EffectClass.RepoFilesOnly, $"--id <approval id> --by <name> {Where}", ApprovalRevokeAsync, DataSchemas.None),
        new Verb("validate", "Check an app folder before publishing: its manifest, its files, and the addresses of other hosts in its built files that the manifest does not ask for (advice; the host's policy is what enforces)", EffectClass.OfflineOnly,
            "--dir <folder> [--manifest <file, default <folder>/app.yaml>]", ValidateAsync,
            DataSchemas.Object([("app", DataSchemas.String), ("outside", DataSchemas.ListOf(DataSchemas.Object([("file", DataSchemas.String), ("line", DataSchemas.Integer), ("origin", DataSchemas.String)], "file", "line", "origin")))], "app", "outside")),
        // apps reach the group's published models now (plan 20 O08c); the verbs that paired a Store say so rather than vanish
        new Verb("store-pair", "Gone: an app's data is the group's published models, so the apps host pairs with no Store", EffectClass.OfflineOnly, Where, GoneAsync, DataSchemas.None),
        new Verb("store-list", "Gone: an app's data is the group's published models, so the apps host pairs with no Store", EffectClass.OfflineOnly, Where, GoneAsync, DataSchemas.None),
        new Verb("store-remove", "Gone: an app's data is the group's published models, so the apps host pairs with no Store", EffectClass.OfflineOnly, Where, GoneAsync, DataSchemas.None),
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

    private static async Task<int> ValidateAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "dir", "manifest", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("dir") is not { Length: > 0 } dir)
        {
            await context.Error.WriteLineAsync("Usage: apps validate --dir <folder> [--manifest <file>]");
            return CliExit.Usage;
        }
        var folder = Path.GetFullPath(dir);
        var manifestPath = parsed.Get("manifest") is { Length: > 0 } m ? Path.GetFullPath(m) : Path.Combine(folder, "app.yaml");
        if (!Directory.Exists(folder) || !File.Exists(manifestPath))
        {
            await context.Error.WriteLineAsync(!Directory.Exists(folder) ? $"{folder} is not a folder." : $"{manifestPath} does not exist.");
            return CliExit.Failure;
        }
        var load = AppManifestLoader.Load(File.ReadAllText(manifestPath));
        if (!load.Ok)
        {
            foreach (var d in load.Diagnostics) await context.Error.WriteLineAsync(d.Message);
            return CliExit.Failure;
        }
        var outside = AppChecks.OutsideUrls(folder, load.Manifest!.Egress ?? AppEgress.None);
        foreach (var o in outside) await context.Out.WriteLineAsync($"{o.File}:{o.Line}  {o.Origin} is not in the manifest's egress; the browser will block it unless the manifest asks and an administrator approves.");
        await context.Out.WriteLineAsync(outside.Count == 0 ? $"{load.Manifest.Name} {load.Manifest.Version}: the manifest is valid and the files reach no other host." : $"{load.Manifest.Name} {load.Manifest.Version}: the manifest is valid; {outside.Count} outside address(es) found.");
        context.Data["app"] = load.Manifest.Name;
        context.Data["outside"] = outside.Select(o => new Dictionary<string, object?> { ["file"] = o.File, ["line"] = o.Line, ["origin"] = o.Origin }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> ApproveAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "app", "version", "by", "note", "egress", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("app") is not { Length: > 0 } app || !int.TryParse(parsed.Get("version"), out var version) || parsed.Get("by") is not { Length: > 0 } by)
        {
            await context.Error.WriteLineAsync($"Usage: apps approve --app <app> --version <n> --by <approver> [--egress <directive>=<origin>[,...]] [--note <text>] {Where}");
            return CliExit.Usage;
        }
        try
        {
            var approval = new AppApprovalStore(new DefinitionStore(IdentityCommand.Open(parsed))).Approve(app, version, by, parsed.Get("note"), egress: AppApprovalStore.ParseEgress(parsed.Get("egress")));
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

    /// <summary>The message of the store-* verbs since the group serves apps' data (plan 20 O08c).</summary>
    public const string StoresGone =
        "The apps host no longer pairs with Store servers: an app reads and writes the models published to this group (dbdatastore model publish <model> --proxy <name>), at /apps/<instance>/_data/api/v1/models/<model>/... "
        + "A pairing made before is not used; remove <data directory>/apps/stores.json, and the host's pairing on the Store (dbdatastore server unpair).";

    private static async Task<int> GoneAsync(string[] args, CliContext context)
    {
        await context.Error.WriteLineAsync(StoresGone);
        return CliExit.Failure;
    }
}
