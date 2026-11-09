using DbDataKit.Cli;
using DbDataKit.Config.Git;
using DbDataKit.State;

namespace DbDataWorkspace.Server;

/// <summary>
/// <c>dbdataworkspace project add|create|list</c> and <c>workspace list</c> on the server's directory, with the server stopped or running: the operator's way to give a new server its first project without a console (the management API
/// does the same for a console, with a token). The rules are the API's: a project's name is checked, a taken name is refused, and a project is a git repository.
/// </summary>
public static class WorkspaceAdminCommand
{
    private const string Where = "[--repo <server directory>]";

    private static readonly string ProjectRow = DataSchemas.Object([("id", DataSchemas.String), ("kind", DataSchemas.String), ("title", DataSchemas.String), ("path", DataSchemas.String)], "id", "kind");

    public static Noun Project(Func<string, string?>? environment) => new("project", "The projects this server hosts",
    [
        new Verb("create", "Make a new project: a git repository in the server's directory with a first commit", EffectClass.RepoFilesOnly, $"<id> [--title <text>] [--kind app] {Where}",
            (args, context) => CreateAsync(args, context, environment), DataSchemas.Object([("id", DataSchemas.String), ("kind", DataSchemas.String)], "id", "kind")),
        new Verb("add", "Host a git repository that is already on this machine, in place (merges land in that folder)", EffectClass.RepoFilesOnly, $"<id> --path <repository folder> [--title <text>] [--kind app] {Where}",
            (args, context) => AddAsync(args, context, environment), DataSchemas.Object([("id", DataSchemas.String), ("kind", DataSchemas.String), ("path", DataSchemas.String)], "id", "kind", "path")),
        new Verb("list", "The projects this server hosts", EffectClass.OfflineOnly, Where,
            (args, context) => ListAsync(args, context, environment), DataSchemas.Object([("projects", DataSchemas.ListOf(ProjectRow))], "projects")),
    ]);

    public static Noun Workspace(Func<string, string?>? environment) => new("workspace", "The drafts people are working on",
    [
        new Verb("list", "The workspaces that exist now, with their owners (a project's with --project)", EffectClass.OfflineOnly, $"[--project <id>] {Where}",
            (args, context) => ListWorkspacesAsync(args, context, environment),
            DataSchemas.Object([("workspaces", DataSchemas.ListOf(DataSchemas.Object([("project", DataSchemas.String), ("id", DataSchemas.String), ("owner", DataSchemas.String), ("branch", DataSchemas.String)], "project", "id", "owner", "branch")))], "workspaces")),
    ]);

    public static Noun Publisher(Func<string, string?>? environment) => new("publisher", "The keys this server publishes with: a key for a target, paired with a Gateway",
    [
        new Verb("pair", "Make the key for a target and pair it with a Gateway (a single-use code made there with `server pair --max-role publisher`); the key stays in this server's secret store and a reference to it is recorded", EffectClass.RepoFilesOnly,
            $"<name> --url <https address of the Gateway's management API> --code <pairing code> [--client-name <text>] {Where}", (args, context) => PairAsync(args, context, environment),
            DataSchemas.Object([("name", DataSchemas.String), ("url", DataSchemas.String), ("secret_ref", DataSchemas.String)], "name", "url", "secret_ref")),
        new Verb("list", "The targets: the reference to each one's key (never the key) and the Gateway it is paired with", EffectClass.OfflineOnly, Where,
            (args, context) => ListKeysAsync(args, context, environment), DataSchemas.Object([("keys", DataSchemas.ListOf(DataSchemas.Object([("name", DataSchemas.String), ("secret_ref", DataSchemas.String), ("url", DataSchemas.String)], "name", "secret_ref")))], "keys")),
    ]);

    private static async Task<int> PairAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "url", "code", "client-name", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1 || parsed.Get("url") is not { Length: > 0 } url || parsed.Get("code") is not { Length: > 0 } code || !Uri.TryCreate(url, UriKind.Absolute, out var address))
        {
            await context.Error.WriteLineAsync($"Usage: publisher pair <name> --url <https address of the Gateway's management API> --code <pairing code> [--client-name <text>] {Where}");
            return CliExit.Usage;
        }
        var directory = DirectoryOf(parsed, environment);
        try
        {
            var (store, _) = Open(directory, environment);
            var target = await new WorkspacePublishing(directory, store).PairAsync(parsed.Words[0], address, code, parsed.Get("client-name"), Operator, context.Cancellation);
            await context.Out.WriteLineAsync($"Paired the publisher key {target.Name} with {target.Url} as '{target.ClientName}' with the roles {string.Join(", ", target.Roles)}; its certificate is pinned: {target.Fingerprint}. The key is in this server's secret store: {WorkspacePublishing.ReferenceFor(target.Name)}.");
            context.Data["name"] = target.Name;
            context.Data["url"] = target.Url;
            context.Data["secret_ref"] = WorkspacePublishing.ReferenceFor(target.Name);
            return CliExit.Ok;
        }
        catch (WorkspacePublishException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return ex.Status == 400 ? CliExit.Usage : CliExit.Failure;
        }
    }

    private static async Task<int> ListKeysAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) { await context.Error.WriteLineAsync($"Usage: publisher list {Where}"); return CliExit.Usage; }
        var directory = DirectoryOf(parsed, environment);
        var (store, _) = Open(directory, environment);
        var publishing = new WorkspacePublishing(directory, store);
        var keys = store.PublisherKeys();
        foreach (var k in keys) await context.Out.WriteLineAsync($"{k.Name}  {k.SecretRef}  {publishing.Find(k.Name)?.Url ?? "(no Gateway paired)"}");
        if (keys.Count == 0) await context.Out.WriteLineAsync("There is no publisher key. Make one with: publisher pair <name> --url <gateway> --code <code>");
        context.Data["keys"] = keys.Select(k => new Dictionary<string, object?> { ["name"] = k.Name, ["secret_ref"] = k.SecretRef, ["url"] = publishing.Find(k.Name)?.Url ?? "" }).ToList();
        return CliExit.Ok;
    }

    private static string Operator => "local:" + Environment.UserName;

    private static string DirectoryOf(CliArgs parsed, Func<string, string?>? environment) =>
        Path.GetFullPath(parsed.Get("repo") is { Length: > 0 } repo ? repo : WorkspaceSettings.DefaultDirectory(environment));

    private static (WorkspaceStore Store, WorkspaceProjects Projects) Open(string directory, Func<string, string?>? environment)
    {
        var store = new WorkspaceStore(WorkspaceState.Open(directory, environment: environment));
        return (store, new WorkspaceProjects(directory, store));
    }

    private static async Task<int> CreateAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "title", "kind", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1)
        {
            await context.Error.WriteLineAsync($"Usage: project create <id> [--title <text>] [--kind app] {Where}");
            return CliExit.Usage;
        }
        var directory = DirectoryOf(parsed, environment);
        try
        {
            var (_, projects) = Open(directory, environment);
            var made = projects.Create(parsed.Words[0], parsed.Get("kind") ?? "app", parsed.Get("title"), new GitAuthor(Environment.UserName, $"{Environment.UserName}@dbdataworkspace.local"), Operator);
            await context.Out.WriteLineAsync($"Made the {made.Kind} project {made.Id} in {projects.RootOf(made)}.");
            context.Data["id"] = made.Id;
            context.Data["kind"] = made.Kind;
            return CliExit.Ok;
        }
        catch (WorkspaceProjectException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return ex.Status == 400 ? CliExit.Usage : CliExit.Failure;
        }
    }

    private static async Task<int> AddAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "path", "title", "kind", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1 || parsed.Get("path") is not { Length: > 0 } path)
        {
            await context.Error.WriteLineAsync($"Usage: project add <id> --path <repository folder> [--title <text>] [--kind app] {Where}");
            return CliExit.Usage;
        }
        try
        {
            var (_, projects) = Open(DirectoryOf(parsed, environment), environment);
            var added = projects.Add(parsed.Words[0], parsed.Get("kind") ?? "app", parsed.Get("title"), path, Operator);
            await context.Out.WriteLineAsync($"Hosting {added.Path} as the {added.Kind} project {added.Id}.");
            context.Data["id"] = added.Id;
            context.Data["kind"] = added.Kind;
            context.Data["path"] = added.Path ?? "";
            return CliExit.Ok;
        }
        catch (WorkspaceProjectException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return ex.Status == 400 ? CliExit.Usage : CliExit.Failure;
        }
    }

    private static async Task<int> ListAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) { await context.Error.WriteLineAsync($"Usage: project list {Where}"); return CliExit.Usage; }
        var (store, projects) = Open(DirectoryOf(parsed, environment), environment);
        var all = store.Projects();
        foreach (var p in all) await context.Out.WriteLineAsync($"{p.Id}  {p.Kind}  {projects.RootOf(p)}");
        if (all.Count == 0) await context.Out.WriteLineAsync("This server hosts no project. Make one with: project create <id>");
        context.Data["projects"] = all.Select(p => new Dictionary<string, object?> { ["id"] = p.Id, ["kind"] = p.Kind, ["title"] = p.Title ?? "", ["path"] = projects.RootOf(p) }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> ListWorkspacesAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "project", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) { await context.Error.WriteLineAsync($"Usage: workspace list [--project <id>] {Where}"); return CliExit.Usage; }
        var (store, _) = Open(DirectoryOf(parsed, environment), environment);
        var all = store.Workspaces(parsed.Get("project"));
        foreach (var w in all) await context.Out.WriteLineAsync($"{w.Project}/{w.Id}  {w.OwnerName}  {w.Branch}");
        if (all.Count == 0) await context.Out.WriteLineAsync("There is no workspace.");
        context.Data["workspaces"] = all.Select(w => new Dictionary<string, object?> { ["project"] = w.Project, ["id"] = w.Id, ["owner"] = w.OwnerName, ["branch"] = w.Branch }).ToList();
        return CliExit.Ok;
    }
}
