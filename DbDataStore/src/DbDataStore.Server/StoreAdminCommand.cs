using DbDataKit.Cli;
using DbDataKit.DataModel.Storage;

namespace DbDataStore.Server;

/// <summary>
/// <c>dbdatastore model put</c> and <c>storage create</c> on the server's directory, with the server stopped or running: the operator's way to give a new server its first model and tables without a console (the management API does the same
/// for a console, with a token). The rules are the API's: a model is checked before it is saved, and storage is created only from the model's current text.
/// </summary>
public static class StoreAdminCommand
{
    private const string Where = "[--repo <server directory>]";

    public static Noun Model() => new("model", "The models this server holds",
    [
        new Verb("put", "Save a model/1 file as a model of this server (it is checked first; the name is the file's name unless --name is given)", EffectClass.RepoFilesOnly, $"--file <model.yaml> [--name <model>] {Where}", PutAsync,
            DataSchemas.Object([("name", DataSchemas.String), ("created", DataSchemas.Boolean)], "name", "created")),
        new Verb("publish", "Make the model as it is now available (plan 20 O10b): its tables are brought to it, this server's copy is pinned to it, and it is sent to its serving groups (or the one --proxy names), which then serve it and enforce its rules; --dry-run says what would change", EffectClass.TargetWrites,
            $"<model> [--proxy <name>] [--dry-run] {Where}", PublishAsync,
            DataSchemas.Object([("model", DataSchemas.String), ("released_version", DataSchemas.String), ("dry_run", DataSchemas.Boolean), ("statements", DataSchemas.Integer), ("groups", DataSchemas.StringList)], "model", "released_version", "dry_run", "statements", "groups")),
        new Verb("unpublish", "Take a model off a serving group; once it is on none, this server reads and writes it again", EffectClass.TargetWrites, $"<model> --proxy <name> {Where}", UnpublishAsync,
            DataSchemas.Object([("model", DataSchemas.String), ("proxy", DataSchemas.String)], "model", "proxy")),
    ]);

    public static Noun Proxy() => new("gateway", "The serving groups this server publishes its models to",
    [
        new Verb("add", "Pair this server with a serving group (a Proxy) as a publishing client, with a code its administrator made (dbdatagateway server pair --max-role publisher)", EffectClass.RepoFilesOnly,
            $"<name> --url <https address> --code <pairing code> {Where}", AddProxyAsync,
            DataSchemas.Object([("name", DataSchemas.String), ("url", DataSchemas.String), ("instance_id", DataSchemas.String)], "name", "url", "instance_id")),
        new Verb("list", "The serving groups this server is paired with", EffectClass.OfflineOnly, Where, ListProxiesAsync,
            DataSchemas.Object([("proxies", DataSchemas.ListOf(DataSchemas.Object([("name", DataSchemas.String), ("url", DataSchemas.String), ("instance_id", DataSchemas.String)], "name", "url", "instance_id")))], "proxies")),
        new Verb("remove", "Forget a serving group (it keeps what was published; unpair this server there too)", EffectClass.RepoFilesOnly, $"<name> {Where}", RemoveProxyAsync,
            DataSchemas.Object([("removed", DataSchemas.String)], "removed")),
    ]);

    private static async Task<int> PublishAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { "dry-run" }, new HashSet<string> { "proxy", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1)
        {
            await context.Error.WriteLineAsync($"Usage: model publish <model> [--proxy <name>] [--dry-run] {Where}");
            return CliExit.Usage;
        }
        var model = parsed.Words[0];
        var dryRun = parsed.Has("dry-run");
        // the operator on the server's machine: the person the group's audit names
        var result = await StorePublisher.For(DirectoryOf(parsed)).PublishAsync(model, parsed.Get("proxy") is { Length: > 0 } proxy ? [proxy] : [], "local:" + Environment.UserName, Environment.UserName, dryRun, context.Cancellation);
        foreach (var statement in result.Detail?["statements"]?.AsArray() ?? []) await context.Out.WriteLineAsync($"  {(string?)statement!["description"]}");
        if (!result.Ok)
        {
            await context.Error.WriteLineAsync(result.Message);
            return CliExit.Failure;
        }
        await context.Out.WriteLineAsync(result.Message);
        context.Data["model"] = model;
        context.Data["released_version"] = (string?)result.Detail?["released_version"] ?? "";
        context.Data["dry_run"] = dryRun;
        context.Data["statements"] = result.Detail?["statements"]?.AsArray().Count ?? 0;
        context.Data["groups"] = (result.Detail?["groups"]?.AsArray() ?? []).Select(g => (string)g!["proxy"]!).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> UnpublishAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "proxy", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1 || parsed.Get("proxy") is not { Length: > 0 } proxy)
        {
            await context.Error.WriteLineAsync($"Usage: model unpublish <model> --proxy <name> {Where}");
            return CliExit.Usage;
        }
        var result = await StorePublisher.For(DirectoryOf(parsed)).UnpublishAsync(parsed.Words[0], proxy, "local:" + Environment.UserName, Environment.UserName, context.Cancellation);
        if (!result.Ok) { await context.Error.WriteLineAsync(result.Message); return CliExit.Failure; }
        await context.Out.WriteLineAsync(result.Message);
        context.Data["model"] = parsed.Words[0];
        context.Data["proxy"] = proxy;
        return CliExit.Ok;
    }

    private static async Task<int> AddProxyAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "url", "code", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1 || parsed.Get("code") is not { Length: > 0 } code
            || !Uri.TryCreate(parsed.Get("url"), UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
        {
            await context.Error.WriteLineAsync($"Usage: proxy add <name> --url <https address> --code <pairing code> {Where}");
            return CliExit.Usage;
        }
        var name = parsed.Words[0];
        if (!StoreModels.IsValidName(name)) { await context.Error.WriteLineAsync($"'{name}' is not a name: lower-case letters, digits, '-' and '_'."); return CliExit.Usage; }
        try
        {
            var proxy = await StorePublisher.For(DirectoryOf(parsed)).PairAsync(name, url, code, context.Cancellation);
            await context.Out.WriteLineAsync($"Paired with {proxy.Url} ({proxy.InstanceId}) as {name}.");
            context.Data["name"] = name;
            context.Data["url"] = proxy.Url;
            context.Data["instance_id"] = proxy.InstanceId;
            return CliExit.Ok;
        }
        catch (DbDataKit.Web.Trust.PairingFailedException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }

    private static async Task<int> ListProxiesAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) { await context.Error.WriteLineAsync($"Usage: proxy list {Where}"); return CliExit.Usage; }
        var all = new StoreProxies(DirectoryOf(parsed)).All();
        foreach (var p in all) await context.Out.WriteLineAsync($"{p.Name}  {p.Url}  {p.InstanceId}");
        if (all.Count == 0) await context.Out.WriteLineAsync("This server is not paired with a serving group.");
        context.Data["proxies"] = all.Select(p => new Dictionary<string, object?> { ["name"] = p.Name, ["url"] = p.Url, ["instance_id"] = p.InstanceId }).ToList();
        return CliExit.Ok;
    }

    private static async Task<int> RemoveProxyAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1) { await context.Error.WriteLineAsync($"Usage: proxy remove <name> {Where}"); return CliExit.Usage; }
        if (!new StoreProxies(DirectoryOf(parsed)).Remove(parsed.Words[0])) { await context.Error.WriteLineAsync($"This server is not paired with '{parsed.Words[0]}'."); return CliExit.Failure; }
        await context.Out.WriteLineAsync($"Forgot {parsed.Words[0]}.");
        context.Data["removed"] = parsed.Words[0];
        return CliExit.Ok;
    }

    public static Noun Storage() => new("storage", "The tables of a model",
    [
        new Verb("create", "Create the tables of a model's own copy on its connection (the connection file is connections/<name>.yaml in the server's directory)", EffectClass.TargetWrites, $"--model <model> {Where}", CreateAsync,
            DataSchemas.Object([("engine", DataSchemas.String), ("statements", DataSchemas.Integer)], "engine", "statements")),
    ]);

    private static string DirectoryOf(CliArgs parsed) => Path.GetFullPath(parsed.Get("repo") is { Length: > 0 } repo ? repo : StoreSettings.DefaultDirectory(null));

    private static async Task<int> PutAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "file", "name", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("file") is not { Length: > 0 } file)
        {
            await context.Error.WriteLineAsync($"Usage: model put --file <model.yaml> [--name <model>] {Where}");
            return CliExit.Usage;
        }
        if (!File.Exists(file)) { await context.Error.WriteLineAsync($"{file} does not exist."); return CliExit.Failure; }
        var name = parsed.Get("name") ?? Path.GetFileNameWithoutExtension(file);
        if (!StoreModels.IsValidName(name)) { await context.Error.WriteLineAsync(StoreModels.NameProblem(name)); return CliExit.Usage; }
        var text = (await File.ReadAllTextAsync(file)).ReplaceLineEndings("\n");
        var result = StoreModels.Check(name, text);
        if (!result.Ok)
        {
            await context.Error.WriteLineAsync($"{name}.yaml is not a valid model, so nothing was saved:");
            foreach (var d in result.Diagnostics) await context.Error.WriteLineAsync("  " + d);
            return CliExit.Failure;
        }
        var directory = DirectoryOf(parsed);
        var path = StoreModels.FileOf(directory, name);
        var existed = File.Exists(path);
        var before = existed ? await File.ReadAllTextAsync(path) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path + ".tmp", text, new System.Text.UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
        StoreModelHistory.RecordSave(directory, name, text, before, "command-line", null);
        await context.Out.WriteLineAsync($"{(existed ? "Updated" : "Saved")} the model {name}.");
        context.Data["name"] = name;
        context.Data["created"] = !existed;
        return CliExit.Ok;
    }

    private static async Task<int> CreateAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "model", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("model") is not { Length: > 0 } name)
        {
            await context.Error.WriteLineAsync($"Usage: storage create --model <model> {Where}");
            return CliExit.Usage;
        }
        var directory = DirectoryOf(parsed);
        if (!StoreModels.IsValidName(name) || !File.Exists(StoreModels.FileOf(directory, name))) { await context.Error.WriteLineAsync($"There is no model '{name}'."); return CliExit.Failure; }
        var check = StoreModels.Check(name, await File.ReadAllTextAsync(StoreModels.FileOf(directory, name)));
        if (!check.Ok) { await context.Error.WriteLineAsync($"{name}.yaml is not a valid model."); return CliExit.Failure; }
        if (new StorePublications(directory).Of(name) is { Count: > 0 } groups)
        {
            await context.Error.WriteLineAsync($"The model '{name}' is published to {string.Join(", ", groups)}: its tables move only by publishing it (dbdatastore model publish {name}).");
            return CliExit.Failure;
        }
        var model = check.Model!;
        try
        {
            var connections = new StoreConnections(directory);
            var plan = ModelStorage.Plan(model, connections.EngineOf(model.Connection), model.Name);
            await using var connection = await connections.OpenAsync(model.Connection, context.Cancellation);
            await ModelStorage.CreateAsync(connection, plan, context.Cancellation);
            StoreInstances.Record(directory, name, StoreInstances.Default, ModelStorage.DefinitionHash(model, plan.Engine), StoreModels.CurrentVersion(directory, name), DateTimeOffset.UtcNow);
            await context.Out.WriteLineAsync($"Created the tables of {name} on {model.Connection} ({plan.Engine}, {plan.Statements.Count} statements).");
            context.Data["engine"] = plan.Engine;
            context.Data["statements"] = plan.Statements.Count;
            return CliExit.Ok;
        }
        catch (StoreConnectionException ex)
        {
            await context.Error.WriteLineAsync(ex.Message);
            return CliExit.Failure;
        }
    }
}
