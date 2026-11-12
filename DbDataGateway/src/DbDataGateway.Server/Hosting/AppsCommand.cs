using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Cli;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// <c>dbdatagateway apps ...</c> (plan 26, AP05f): the apps, instances and navigation sets the apps host serves, in the Gateway's state. An app's data is the models published to the group (plan 20 O08c); the <c>store-*</c>
/// verbs that paired the host with a Store server are gone and say so.
/// </summary>
public static class AppsCommand
{
    private const string Where = "[--repo <data directory>]";

    /// <summary>The options of the verbs that can act on a Gateway elsewhere (plan 26, AP26): its address or the name it was paired under, the single-use code of the first pairing, and where this machine keeps its pairings.</summary>
    private const string Remote = "[--to <gateway address or paired name> [--code <pairing code>] [--alias <name>] [--client-name <text>] [--client-dir <folder>]]";

    private static readonly string[] RemoteOptions = ["to", "code", "alias", "client-name", "client-dir"];

    private static HashSet<string> Valued(params string[] names) => [.. names, "repo", .. RemoteOptions];

    private sealed record RemoteTarget(AppsRemote Client, string Server, string? Code, string? Alias, string? ClientName);

    /// <summary>The Gateway named by <paramref name="option"/> (<c>--to</c>; <c>--on</c> for rollback, whose <c>--to</c> is a version), or null for the local verb. Naming both a Gateway and a data directory is a mistake and is said so.</summary>
    private static RemoteTarget? RemoteOf(CliArgs parsed, string option = "to")
    {
        if (parsed.Get(option) is not { Length: > 0 } server) return null;
        var directory = parsed.Get("client-dir") is { Length: > 0 } named ? named : AppsClientStore.DefaultDirectory();
        return new RemoteTarget(new AppsRemote(new AppsClientStore(directory)), server, parsed.Get("code"), parsed.Get("alias"), parsed.Get("client-name"));
    }

    private static bool RemoteMixedWithRepo(CliArgs parsed, string option = "to") => parsed.Get(option) is { Length: > 0 } && parsed.Get("repo") is { Length: > 0 };

    private const string MixedMessage = "--repo names a data directory on this machine and --to a Gateway elsewhere; use one.";

    private static string Describe(RemoteTarget remote, JsonObject answer) =>
        $"to {remote.Server}{(answer["published_by"]?.GetValue<string>() is { } by ? $" as {by}" : "")}";

    public static Noun Noun() => new("apps", "The apps host this gateway serves: its apps, instances and navigation sets",
    [
        new Verb("init", "Make a new app folder from a sample (territory-notes, territory-notes-src or front-portal): a manifest and a starter file set that validate; nothing is read from the network", EffectClass.RepoFilesOnly,
            $"--template <{string.Join("|", AppsBuild.Templates.Keys)}> --dir <new folder> [--name <app name>]", AppsBuild.InitAsync,
            DataSchemas.Object([("template", DataSchemas.String), ("dir", DataSchemas.String), ("files", DataSchemas.ListOf(DataSchemas.String))], "template", "dir", "files")),
        new Verb("new", "Start an app from the app template (React, the Store controls, the visuals and the typed clients; plan 22a GP32): the same as `init --template app`. `build` then bundles it from the vendored package store", EffectClass.RepoFilesOnly,
            "--dir <new folder> [--name <app name>]", AppsBuild.NewAsync,
            DataSchemas.Object([("template", DataSchemas.String), ("dir", DataSchemas.String), ("files", DataSchemas.ListOf(DataSchemas.String))], "template", "dir", "files")),
        new Verb("store-verify", "Check the vendored package store the app build uses against its store.json (a changed, added or missing file is named) and print what an app may import and the manifest's digest (plan 22a GP33)", EffectClass.OfflineOnly,
            "[--store <package store folder>] [--repo <data directory>]", AppsBuild.StoreVerifyAsync,
            DataSchemas.Object([("store", DataSchemas.String), ("packages", DataSchemas.Integer), ("files", DataSchemas.Integer), ("digest", DataSchemas.String)], "store", "packages", "files", "digest")),
        new Verb("build", "Build an app's source tree into a folder that validates and publishes: bundles src/main.ts (or --entry) with the pinned Bun, copies the files that are not source, runs the built-in checks on the result. With a package store (--store, or apps.packageStore in server.yaml) it installs nothing, builds from the vendored packages with no network, and refuses an import that is not in the default library or the allow-list (apps.allowedPackages)", EffectClass.RepoFilesOnly,
            "--src <source folder> --out <build folder> [--entry <script>] [--manifest <file>] [--no-minify] [--offline] [--store <package store folder>] [--allow <package>[,...]] [--isolation <auto|required|none>] [--repo <data directory>] [--bun <path>] [--timeout <seconds>]", AppsBuild.BuildAsync,
            DataSchemas.Object([("app", DataSchemas.String), ("out", DataSchemas.String), ("files", DataSchemas.Integer), ("bundled", DataSchemas.ListOf(DataSchemas.String))], "app", "out", "files", "bundled")),
        new Verb("publish", "Publish an app version from a folder: its manifest (app.yaml) and the files the host serves. With --to, to a Gateway elsewhere, as a paired client", EffectClass.RepoFilesOnly, $"--dir <folder> [--manifest <file, default <folder>/app.yaml>] [--note <text>] ({Where} | {Remote})", PublishAsync,
            DataSchemas.Object([("app", DataSchemas.String), ("version", DataSchemas.Integer), ("files", DataSchemas.Integer)], "app", "version", "files")),
        new Verb("instance-save", "Make or change an instance of a published app: its version pin, title, settings and data bindings (a new instance version; the host serves it at once)", EffectClass.RepoFilesOnly,
            $"--name <instance> --app <app> [--pin <version>] [--title <text>] [--setting k=v[,k=v]] [--bind model=storeInstance[,model=storeInstance]] ({Where} | {Remote})", InstanceSaveAsync,
            DataSchemas.Object([("name", DataSchemas.String), ("app", DataSchemas.String), ("version", DataSchemas.Integer)], "name", "app", "version")),
        new Verb("instance-list", "The instances the host serves, with the app and version each runs", EffectClass.OfflineOnly, Where, InstanceListAsync,
            DataSchemas.Object([("instances", DataSchemas.ListOf(DataSchemas.Object([("name", DataSchemas.String), ("app", DataSchemas.String), ("pin", DataSchemas.Integer), ("title", DataSchemas.String)], "name", "app")))], "instances")),
        new Verb("nav-save", "Make or change a navigation set: a titled, ordered menu the shell shows in each member instance (a person sees only the members they may open)", EffectClass.RepoFilesOnly,
            $"--name <set> --title <text> --item <instance[=label]>[,<instance[=label]>...] {Where}", NavSaveAsync,
            DataSchemas.Object([("name", DataSchemas.String), ("version", DataSchemas.Integer)], "name", "version")),
        new Verb("nav-list", "The navigation sets and their members", EffectClass.OfflineOnly, Where, NavListAsync,
            DataSchemas.Object([("sets", DataSchemas.ListOf(DataSchemas.Object([("name", DataSchemas.String), ("title", DataSchemas.String), ("instances", DataSchemas.ListOf(DataSchemas.String))], "name", "title", "instances")))], "sets")),
        new Verb("list", "The published apps with the version each serves, and with --app every version of one (what a rollback or a pin can name)", EffectClass.OfflineOnly, $"[--app <app>] ({Where} | {Remote})", ListAsync,
            DataSchemas.Object([("apps", DataSchemas.ListOf(DataSchemas.Object([("name", DataSchemas.String), ("version", DataSchemas.Integer), ("versions", DataSchemas.ListOf(DataSchemas.Integer))], "name", "version", "versions")))], "apps")),
        new Verb("rollback", "Move an app's current version back to the one before it, or to --to <version>; instances pinned to a version stay, the others serve it at once", EffectClass.RepoFilesOnly, $"--app <app> [--to <version>] ({Where} | {Remote.Replace("--to ", "--on ")})", RollbackAsync,
            DataSchemas.Object([("app", DataSchemas.String), ("version", DataSchemas.Integer)], "app", "version")),
        new Verb("remove", "Stop serving an app (its versions and approvals stay on record); refused while an instance of it exists", EffectClass.RepoFilesOnly, $"--app <app> {Where}", RemoveAppAsync, DataSchemas.None),
        new Verb("instance-remove", "Stop serving an instance: its address answers 404 (its storage and history stay); refused while a portal mounts it", EffectClass.RepoFilesOnly, $"--name <instance> {Where}", RemoveInstanceAsync, DataSchemas.None),
        new Verb("nav-remove", "Stop serving a navigation set; its instances are not touched", EffectClass.RepoFilesOnly, $"--name <set> {Where}", RemoveNavAsync, DataSchemas.None),
        new Verb("approve", "Approve the exact bytes of a trusted app, as an identity-home user who holds the Approver role (not the author): the hash of a built folder (--path), a hash as given (--hash), or an already published version's hash (--app --version). Publishing a trusted app needs an approval for its hash", EffectClass.RepoFilesOnly,
            $"(--path <folder> [--manifest <file>] | --hash <hash> --app <app> | --app <app> --version <n>) --by <approver> [--egress <directive>=<origin>[,...]] [--note <text>] ({Where} | {Remote})", ApproveAsync,
            DataSchemas.Object([("id", DataSchemas.String), ("app", DataSchemas.String), ("version", DataSchemas.String), ("hash", DataSchemas.String)], "id", "app", "version", "hash")),
        new Verb("approval-list", "The approvals on record, with who gave them and whether they still stand", EffectClass.OfflineOnly, Where, ApprovalListAsync,
            DataSchemas.Object([("approvals", DataSchemas.ListOf(DataSchemas.Object([("id", DataSchemas.String), ("app", DataSchemas.String), ("version", DataSchemas.String), ("approved_by", DataSchemas.String), ("hash", DataSchemas.String), ("revoked", DataSchemas.Boolean)], "id", "app", "version", "approved_by", "hash", "revoked")))], "approvals")),
        new Verb("approval-revoke", "End an approval; the app stops running as trusted. The record stays", EffectClass.RepoFilesOnly, $"--id <approval id> --by <name> {Where}", ApprovalRevokeAsync, DataSchemas.None),
        new Verb("validate", "Check an app folder before publishing: its manifest, its files, and the addresses of other hosts in its built files that the manifest does not ask for (advice; the host's policy is what enforces)", EffectClass.OfflineOnly,
            "--dir <folder> [--manifest <file, default <folder>/app.yaml>]", ValidateAsync,
            DataSchemas.Object([("app", DataSchemas.String), ("outside", DataSchemas.ListOf(DataSchemas.Object([("file", DataSchemas.String), ("line", DataSchemas.Integer), ("origin", DataSchemas.String)], "file", "line", "origin")))], "app", "outside")),
        // apps reach the group's published models now (plan 20 O08c); the verbs that paired a Store say so rather than vanish
        new Verb("store-pair", "Gone: an app's data is the group's published models, so the apps host pairs with no Store", EffectClass.OfflineOnly, Where, GoneAsync, DataSchemas.None),
        new Verb("store-list", "Gone: an app's data is the group's published models, so the apps host pairs with no Store", EffectClass.OfflineOnly, Where, GoneAsync, DataSchemas.None),
        new Verb("store-remove", "Gone: an app's data is the group's published models, so the apps host pairs with no Store", EffectClass.OfflineOnly, Where, GoneAsync, DataSchemas.None),
    ]);

    private static GatewayOptions OptionsOf(CliArgs parsed)
    {
        var directory = parsed.Get("repo") is { Length: > 0 } repo ? Path.GetFullPath(repo) : null;
        var options = GatewayConfiguration.LoadOptions(directory);
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
        var parsed = CliArgs.Parse(args, new HashSet<string>(), Valued("dir", "manifest", "note"));
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("dir") is not { Length: > 0 } dir)
        {
            await context.Error.WriteLineAsync($"Usage: apps publish --dir <folder> [--manifest <file>] [--note <text>] ({Where} | {Remote})");
            return CliExit.Usage;
        }
        if (RemoteMixedWithRepo(parsed)) { await context.Error.WriteLineAsync(MixedMessage); return CliExit.Usage; }
        var folder = Path.GetFullPath(dir);
        var manifestPath = parsed.Get("manifest") is { Length: > 0 } m ? Path.GetFullPath(m) : Path.Combine(folder, "app.yaml");
        if (!Directory.Exists(folder) || !File.Exists(manifestPath))
        {
            await context.Error.WriteLineAsync(!Directory.Exists(folder) ? $"{folder} is not a folder." : $"{manifestPath} does not exist: an app's manifest is app.yaml in its folder, or name it with --manifest.");
            return CliExit.Failure;
        }
        try
        {
            // the manifest is read from where it is; the files are the folder's without it (the same package `apps approve --path` hashes)
            var package = AppPackage.FromFolder(folder, manifestPath);
            if (RemoteOf(parsed) is { } remote)
            {
                var answer = (await remote.Client.PublishAsync(remote.Server, remote.Code, remote.Alias, remote.ClientName, package, parsed.Get("note"), line => context.Out.WriteLine(line), context.Cancellation)).Body;
                await context.Out.WriteLineAsync($"Published {(string?)answer["name"]} version {(int?)answer["version"]} ({package.Files.Count} files) {Describe(remote, answer)}.");
                context.Data["app"] = (string?)answer["name"];
                context.Data["version"] = (int?)answer["version"];
                context.Data["files"] = package.Files.Count;
                return CliExit.Ok;
            }
            var definitions = new DefinitionStore(IdentityCommand.Open(parsed));
            var version = new AppInstanceStore(definitions).PublishApp(package, Environment.UserName, note: parsed.Get("note"), approvals: new AppApprovalStore(definitions));
            await context.Out.WriteLineAsync($"Published {version.Name} version {version.Version} ({package.Files.Count} files).");
            context.Data["app"] = version.Name;
            context.Data["version"] = version.Version;
            context.Data["files"] = package.Files.Count;
            return CliExit.Ok;
        }
        catch (Exception ex) when (ex is AppInstanceException or AppPackageException or AppsRemoteException)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> InstanceSaveAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), Valued("name", "app", "pin", "title", "setting", "bind"));
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
            await context.Error.WriteLineAsync(bad1 ?? bad2 ?? $"Usage: apps instance-save --name <instance> --app <app> [--pin <version>] [--title <text>] [--setting k=v] [--bind model=instance] ({Where} | {Remote})");
            return CliExit.Usage;
        }
        if (RemoteMixedWithRepo(parsed)) { await context.Error.WriteLineAsync(MixedMessage); return CliExit.Usage; }
        try
        {
            if (RemoteOf(parsed) is { } remote)
            {
                var answer = (await remote.Client.SaveInstanceAsync(remote.Server, remote.Code, remote.Alias, remote.ClientName, new AppInstance(name, app, pin, parsed.Get("title"), settings, bindings), line => context.Out.WriteLine(line), context.Cancellation)).Body;
                await context.Out.WriteLineAsync($"Saved instance {name} of {app} (version {(int?)answer["version"]}) on {remote.Server}. It is served at /apps/{name}/ by that Gateway's apps host.");
                context.Data["name"] = name;
                context.Data["app"] = app;
                context.Data["version"] = (int?)answer["version"];
                return CliExit.Ok;
            }
            var saved = new AppInstanceStore(DbDataGateway.Server.Serving.ServingKinds.StoreOf(IdentityCommand.Open(parsed))).Save(new AppInstance(name, app, pin, parsed.Get("title"), settings, bindings), Environment.UserName);
            await context.Out.WriteLineAsync($"Saved instance {name} of {app} (version {saved.Version}). It is served at /apps/{name}/.");
            context.Data["name"] = name;
            context.Data["app"] = app;
            context.Data["version"] = saved.Version;
            return CliExit.Ok;
        }
        catch (Exception ex) when (ex is AppInstanceException or AppsRemoteException)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> InstanceListAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        var all = new AppInstanceStore(DbDataGateway.Server.Serving.ServingKinds.StoreOf(IdentityCommand.Open(parsed))).List();
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
            var saved = new AppInstanceStore(DbDataGateway.Server.Serving.ServingKinds.StoreOf(IdentityCommand.Open(parsed))).SaveNavigation(new NavigationSet(name, title, entries), Environment.UserName);
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
        var sets = new AppInstanceStore(DbDataGateway.Server.Serving.ServingKinds.StoreOf(IdentityCommand.Open(parsed))).NavigationSets();
        foreach (var set in sets) await context.Out.WriteLineAsync($"{set.Name}  {set.Title}  {string.Join(", ", set.Entries.Select(e => e.Instance))}");
        if (sets.Count == 0) await context.Out.WriteLineAsync("No navigation set is published. Make one with: apps nav-save --name <set> --title <text> --item <instance>,<instance>");
        context.Data["sets"] = sets.Select(s => new Dictionary<string, object?> { ["name"] = s.Name, ["title"] = s.Title, ["instances"] = s.Entries.Select(e => e.Instance).ToList() }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> ListAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), Valued("app"));
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Usage: apps list [--app <app>] ({Where} | {Remote})");
            return CliExit.Usage;
        }
        if (RemoteMixedWithRepo(parsed)) { await context.Error.WriteLineAsync(MixedMessage); return CliExit.Usage; }
        if (RemoteOf(parsed) is { } remote)
        {
            try
            {
                var answer = (await remote.Client.ListAsync(remote.Server, remote.Code, remote.Alias, remote.ClientName, line => context.Out.WriteLine(line), context.Cancellation)).Body;
                var listed = new List<Dictionary<string, object?>>();
                foreach (var item in answer["apps"]?.AsArray() ?? [])
                {
                    var versions = item!["versions"]!.AsArray().Select(v => (int)v!).ToList();
                    if (parsed.Get("app") is { Length: > 0 } only && (string?)item["name"] != only) continue;
                    await context.Out.WriteLineAsync($"{(string?)item["name"]}  serves version {(int?)item["version"]}  (versions: {string.Join(", ", versions)})");
                    listed.Add(new Dictionary<string, object?> { ["name"] = (string?)item["name"], ["version"] = (int?)item["version"], ["versions"] = versions });
                }
                if (listed.Count == 0) await context.Out.WriteLineAsync($"No app is published on {remote.Server}.");
                context.Data["apps"] = listed;
                return CliExit.Ok;
            }
            catch (AppsRemoteException ex)
            {
                await context.Error.WriteLineAsync(ex.Message);
                return CliExit.Failure;
            }
        }
        var store = new AppInstanceStore(DbDataGateway.Server.Serving.ServingKinds.StoreOf(IdentityCommand.Open(parsed)));
        var current = store.Apps().Where(a => parsed.Get("app") is not { Length: > 0 } only || a.Name == only).ToList();
        var rows = new List<Dictionary<string, object?>>();
        foreach (var app in current)
        {
            var versions = store.AppVersions(app.Name);
            await context.Out.WriteLineAsync($"{app.Name}  serves version {app.Version}  (versions: {string.Join(", ", versions.Select(v => v.Version))})");
            if (parsed.Get("app") is { Length: > 0 })
                foreach (var v in versions) await context.Out.WriteLineAsync($"  {v.Version}  {v.PublishedAt:yyyy-MM-dd HH:mm}  by {v.PublishedBy}  {v.Hash[..12]}{(v.Note is null ? "" : "  " + v.Note)}{(v.Version == app.Version ? "  <- serving" : "")}");
            rows.Add(new Dictionary<string, object?> { ["name"] = app.Name, ["version"] = app.Version, ["versions"] = versions.Select(v => v.Version).ToList() });
        }
        if (current.Count == 0) await context.Out.WriteLineAsync("No app is published. Publish one with: apps publish --dir <folder>");
        context.Data["apps"] = rows;
        return CliExit.Ok;
    }

    private static async Task<int> RollbackAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "app", "to", "repo", "on", "code", "alias", "client-name", "client-dir" });
        int? to = null;
        if (parsed.Get("to") is { Length: > 0 } toText)
        {
            if (!int.TryParse(toText, out var n) || n < 1) { await context.Error.WriteLineAsync($"'{toText}' is not a version number."); return CliExit.Usage; }
            to = n;
        }
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("app") is not { Length: > 0 } app)
        {
            await context.Error.WriteLineAsync($"Usage: apps rollback --app <app> [--to <version>] ({Where} | {Remote.Replace("--to ", "--on ")})");
            return CliExit.Usage;
        }
        if (RemoteMixedWithRepo(parsed, "on")) { await context.Error.WriteLineAsync(MixedMessage.Replace("--to", "--on")); return CliExit.Usage; }
        try
        {
            if (RemoteOf(parsed, "on") is { } remote)
            {
                var answer = (await remote.Client.RollbackAsync(remote.Server, remote.Code, remote.Alias, remote.ClientName, app, to, line => context.Out.WriteLine(line), context.Cancellation)).Body;
                await context.Out.WriteLineAsync($"{app} now serves version {(int?)answer["version"]} on {remote.Server}. Instances pinned to a version are unchanged.");
                context.Data["app"] = app;
                context.Data["version"] = (int?)answer["version"];
                return CliExit.Ok;
            }
            var moved = new AppInstanceStore(DbDataGateway.Server.Serving.ServingKinds.StoreOf(IdentityCommand.Open(parsed))).MoveApp(app, Environment.UserName, to);
            await context.Out.WriteLineAsync($"{app} now serves version {moved.Version}. Instances pinned to a version are unchanged; a trusted app runs only while an approval stands for these exact files.");
            context.Data["app"] = app;
            context.Data["version"] = moved.Version;
            return CliExit.Ok;
        }
        catch (Exception ex) when (ex is AppInstanceException or AppsRemoteException)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> RemoveAsync(string[] args, CliContext context, string noun, string option, string usage, Func<AppInstanceStore, string, string, bool> retire)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { option, "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get(option) is not { Length: > 0 } name)
        {
            await context.Error.WriteLineAsync($"Usage: {usage} {Where}");
            return CliExit.Usage;
        }
        try
        {
            var removed = retire(new AppInstanceStore(DbDataGateway.Server.Serving.ServingKinds.StoreOf(IdentityCommand.Open(parsed))), name, Environment.UserName);
            await context.Out.WriteLineAsync(removed ? $"The {noun} {name} is no longer served." : $"The {noun} {name} was not being served.");
            return removed ? CliExit.Ok : CliExit.Failure;
        }
        catch (AppInstanceException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static Task<int> RemoveAppAsync(string[] args, CliContext context) => RemoveAsync(args, context, "app", "app", "apps remove --app <app>", (s, n, by) => s.RetireApp(n, by));

    private static Task<int> RemoveInstanceAsync(string[] args, CliContext context) => RemoveAsync(args, context, "instance", "name", "apps instance-remove --name <instance>", (s, n, by) => s.RetireInstance(n, by));

    private static Task<int> RemoveNavAsync(string[] args, CliContext context) => RemoveAsync(args, context, "navigation set", "name", "apps nav-remove --name <set>", (s, n, by) => s.RetireNavigation(n, by));

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
        var parsed = CliArgs.Parse(args, new HashSet<string>(), Valued("path", "manifest", "hash", "app", "version", "by", "note", "egress"));
        const string usage = $"Usage: apps approve (--path <folder> [--manifest <file>] | --hash <hash> --app <app> | --app <app> --version <n>) --by <approver> [--egress <directive>=<origin>[,...]] [--note <text>] ({Where} | {Remote})";
        var path = parsed.Get("path");
        var hash = parsed.Get("hash");
        var app = parsed.Get("app");
        var versionText = parsed.Get("version");
        var modes = (path is { Length: > 0 } ? 1 : 0) + (hash is { Length: > 0 } ? 1 : 0) + (versionText is { Length: > 0 } ? 1 : 0);
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("by") is not { Length: > 0 } by || modes != 1
            || (hash is { Length: > 0 } && app is not { Length: > 0 }) || (versionText is { Length: > 0 } && app is not { Length: > 0 }) || (parsed.Get("manifest") is { Length: > 0 } && path is not { Length: > 0 }))
        {
            await context.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }
        if (RemoteMixedWithRepo(parsed)) { await context.Error.WriteLineAsync(MixedMessage); return CliExit.Usage; }
        try
        {
            if (RemoteOf(parsed) is { } remote) return await ApproveRemoteAsync(parsed, remote, by, path, hash, app, versionText, context);
            var options = OptionsOf(parsed);
            var state = IdentityCommand.OpenState(options);
            var approvals = new AppApprovalStore(new DefinitionStore(state), approvers: HomeApprovers.Open(state, options));
            var egress = AppApprovalStore.ParseEgress(parsed.Get("egress"));
            AppApproval approval;
            if (path is { Length: > 0 })
            {
                var folder = Path.GetFullPath(path);
                var manifestPath = parsed.Get("manifest") is { Length: > 0 } m ? Path.GetFullPath(m) : Path.Combine(folder, "app.yaml");
                if (!Directory.Exists(folder) || !File.Exists(manifestPath))
                {
                    await context.Error.WriteLineAsync(!Directory.Exists(folder) ? $"{folder} is not a folder." : $"{manifestPath} does not exist: an app's manifest is app.yaml in its folder, or name it with --manifest.");
                    return CliExit.Failure;
                }
                // the author is whoever publishes from this machine: the same name `apps publish` records, so one person cannot do both
                approval = approvals.Approve(AppPackage.FromFolder(folder, manifestPath), Environment.UserName, by, parsed.Get("note"), egress: egress);
            }
            else if (hash is { Length: > 0 })
                approval = approvals.Approve(app!, hash, Environment.UserName, by, parsed.Get("note"), egress);
            else
            {
                if (!int.TryParse(versionText, out var version) || version < 1) { await context.Error.WriteLineAsync($"'{versionText}' is not a version number."); return CliExit.Usage; }
                approval = approvals.Approve(app!, version, by, parsed.Get("note"), egress: egress);
            }
            await context.Out.WriteLineAsync($"Approved {approval.App} {approval.Version} ({approval.Id}) as {approval.ApprovedBy}. It stands for hash {approval.ContentHash}; publishing exactly those files and manifest needs no other approval.");
            context.Data["id"] = approval.Id;
            context.Data["app"] = approval.App;
            context.Data["version"] = approval.Version;
            context.Data["hash"] = approval.ContentHash;
            return CliExit.Ok;
        }
        catch (Exception ex) when (ex is AppInstanceException or AppPackageException or AppsRemoteException)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    /// <summary>Approval at a Gateway elsewhere (AP26): the Gateway checks that <c>--by</c> is a user of its identity home who holds the Approver role and is not the author, and that this client's pairing allows approving. The author of a built folder is this paired client.</summary>
    private static async Task<int> ApproveRemoteAsync(CliArgs parsed, RemoteTarget remote, string by, string? path, string? hash, string? app, string? versionText, CliContext context)
    {
        JsonObject what;
        if (path is { Length: > 0 })
        {
            var folder = Path.GetFullPath(path);
            var manifestPath = parsed.Get("manifest") is { Length: > 0 } m ? Path.GetFullPath(m) : Path.Combine(folder, "app.yaml");
            if (!Directory.Exists(folder) || !File.Exists(manifestPath))
            {
                await context.Error.WriteLineAsync(!Directory.Exists(folder) ? $"{folder} is not a folder." : $"{manifestPath} does not exist: an app's manifest is app.yaml in its folder, or name it with --manifest.");
                return CliExit.Failure;
            }
            what = AppsRemote.Encode(AppPackage.FromFolder(folder, manifestPath));
        }
        else if (hash is { Length: > 0 }) what = new JsonObject { ["app"] = app, ["hash"] = hash };
        else
        {
            if (!int.TryParse(versionText, out var version) || version < 1) { await context.Error.WriteLineAsync($"'{versionText}' is not a version number."); return CliExit.Usage; }
            what = new JsonObject { ["app"] = app, ["version"] = version };
        }
        var answer = (await remote.Client.ApproveAsync(remote.Server, remote.Code, remote.Alias, remote.ClientName, what, by, parsed.Get("note"), parsed.Get("egress"), line => context.Out.WriteLine(line), context.Cancellation)).Body;
        await context.Out.WriteLineAsync($"Approved {(string?)answer["app"]} {(string?)answer["version"]} ({(string?)answer["id"]}) as {(string?)answer["approved_by"]} on {remote.Server}. It stands for hash {(string?)answer["hash"]}; publishing exactly those files and manifest needs no other approval.");
        context.Data["id"] = (string?)answer["id"];
        context.Data["app"] = (string?)answer["app"];
        context.Data["version"] = (string?)answer["version"];
        context.Data["hash"] = (string?)answer["hash"];
        return CliExit.Ok;
    }

    private static async Task<int> ApprovalListAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        var all = new AppApprovalStore(new DefinitionStore(IdentityCommand.Open(parsed))).All();
        foreach (var a in all) await context.Out.WriteLineAsync($"{a.Id}  {a.App} {a.Version}  approved by {a.ApprovedBy} on {a.ApprovedAt:yyyy-MM-dd}  hash {a.ContentHash[..12]}{(a.PublishedVersion is { } pv ? $"  published as version {pv}" : "")}{(a.RevokedAt is null ? "" : "  revoked")}");
        if (all.Count == 0) await context.Out.WriteLineAsync("No approval is on record. Approve a trusted app's built folder with: apps approve --path <folder> --by <approver>");
        context.Data["approvals"] = all.Select(a => new Dictionary<string, object?> { ["id"] = a.Id, ["app"] = a.App, ["version"] = a.Version, ["approved_by"] = a.ApprovedBy, ["hash"] = a.ContentHash, ["revoked"] = a.RevokedAt is not null }).ToList();
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
            var options = OptionsOf(parsed);
            var state = IdentityCommand.OpenState(options);
            new AppApprovalStore(new DefinitionStore(state), approvers: HomeApprovers.Open(state, options)).Revoke(id, by);
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
