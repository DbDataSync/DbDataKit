using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.DataModel;

namespace DbDataStore.Server;

/// <summary>An instance of a model: its name and, for a model that keeps each instance on a connection of its own, that connection (plan 26 section 6.2, Z12).</summary>
public sealed record StoreInstance(string Name, string? Connection, string? Definition = null, DateTimeOffset? AppliedAt = null, string? ModelVersion = null);

/// <summary>
/// The instances a server holds, one small file each (<c>instances/&lt;model&gt;/&lt;instance&gt;.json</c> in the server's directory). The model decides how its instances are kept apart; this only remembers which exist.
/// The name <c>default</c> is always the model's own copy and needs no file. Removing an instance forgets it and never touches its data.
/// </summary>
public static class StoreInstances
{
    public const string Folder = "instances";
    public const string Default = "default";

    public static string FileOf(string directory, string model, string instance) => Path.Combine(directory, Folder, model, instance + ".json");

    public static IReadOnlyList<StoreInstance> List(string directory, string model)
    {
        var folder = Path.Combine(directory, Folder, model);
        if (!Directory.Exists(folder)) return [];
        return [.. Directory.EnumerateFiles(folder, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Where(n => StoreModels.IsValidName(n) && n != Default).Order(StringComparer.Ordinal).Select(n => Find(directory, model, n)!)];
    }

    public static StoreInstance? Find(string directory, string model, string instance)
    {
        var file = FileOf(directory, model, instance);
        if (!StoreModels.IsValidName(instance) || !File.Exists(file)) return null;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(file));
            return new StoreInstance(instance, node?["connection"]?.GetValue<string>(), node?["definition"]?.GetValue<string>(), node?["applied_at"] is { } at ? DateTimeOffset.Parse(at.GetValue<string>()) : null, node?["model_version"]?.GetValue<string>());
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { return new StoreInstance(instance, null); }
    }

    public static void Save(string directory, string model, StoreInstance instance)
    {
        var file = FileOf(directory, model, instance.Name);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, new JsonObject { ["connection"] = instance.Connection, ["definition"] = instance.Definition, ["applied_at"] = instance.AppliedAt?.ToString("O"), ["model_version"] = instance.ModelVersion }.ToJsonString());
        File.Move(temporary, file, overwrite: true);
    }

    /// <summary>Notes the structure an instance's tables were made or last migrated at (the model's definition hash) and the saved text of the model that went with it. The model's own copy gets a file of its own, <c>default</c>.</summary>
    public static void Record(string directory, string model, string instance, string definition, string modelVersion, DateTimeOffset at) =>
        Save(directory, model, (Find(directory, model, instance) ?? new StoreInstance(instance, null)) with { Definition = definition, AppliedAt = at, ModelVersion = modelVersion });

    /// <summary>Where an instance stands against the model's current structure: <c>current</c>, <c>behind</c>, or <c>unknown</c> (made before the structure was recorded).</summary>
    public static string StatusOf(StoreInstance? instance, string currentDefinition) =>
        instance?.Definition is null ? "unknown" : instance.Definition == currentDefinition ? "current" : "behind";

    public static bool Delete(string directory, string model, string instance)
    {
        var file = FileOf(directory, model, instance);
        if (!File.Exists(file)) return false;
        File.Delete(file);
        return true;
    }

    /// <summary>Where an instance of a model is kept: the connection and the schema, and for a model that shares its tables the instance id the writer stamps and filters by; or why it cannot be reached.</summary>
    public static (string? Connection, string? Schema, string? Instance, string? Problem) Resolve(string directory, DataModel model, string? instance)
    {
        var own = instance is null || instance == Default;
        if (own) return (model.Connection, model.Name, model.Instances == InstanceMode.Column ? Default : null, null);
        if (model.Instances == InstanceMode.None) return (null, null, null, $"The model '{model.Name}' keeps one copy only; it has no instances. Set `storage.instances` in the model to keep more.");
        if (Find(directory, model.Name, instance!) is not { } found) return (null, null, null, $"The model '{model.Name}' has no instance '{instance}'.");
        var schema = model.SchemaOf(instance, out var problem);
        if (schema is null) return (null, null, null, problem);
        return model.Instances switch
        {
            InstanceMode.Connection => found.Connection is { } connection ? (connection, schema, null, null) : (null, null, null, $"The instance '{instance}' names no connection."),
            InstanceMode.Column => (model.Connection, model.Name, instance, null),
            _ => (model.Connection, schema, null, null),
        };
    }
}
