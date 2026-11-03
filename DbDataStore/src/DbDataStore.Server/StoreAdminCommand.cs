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
    ]);

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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path + ".tmp", text, new System.Text.UTF8Encoding(false));
        File.Move(path + ".tmp", path, true);
        StoreModels.Snapshot(directory, name, text);
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
