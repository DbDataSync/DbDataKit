using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;
using DbDataKit.DataModel;
using DbDataKit.Store;

namespace DbDataStore.Server;

/// <summary>
/// The models a DbDataStore server holds (plan 19, Z08): one <c>model/1</c> file per model in the <c>models</c> folder of the server's directory, named <c>&lt;model&gt;.yaml</c>, the model's own <c>name</c> the same.
/// A file is saved only if it loads without errors, so what is on disk is always a model the server can use; a rejected file answers with the loader's diagnostics, each naming the file and line.
/// Since plan 33 SC23 deleted the Store server, the folder is the command line's (<c>dbdatastore model put</c>); a Store model is authored in a store project of the Workspace.
/// </summary>
public static partial class StoreModels
{
    public const string Folder = "models";

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex NamePattern();

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    public static string FileOf(string directory, string name) => Path.Combine(directory, Folder, name + ".yaml");

    /// <summary>The version of a model's text: its SHA-256. Every text that is saved is kept under it, so an instance that has not been migrated yet keeps running the version it was made or last migrated at (plan 26, Z13).</summary>
    public static string VersionOf(string text) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string VersionFileOf(string directory, string name, string version) => Path.Combine(directory, Folder, "versions", name, version + ".yaml");

    /// <summary>Keeps a text under its version if it is not kept yet, and returns the version.</summary>
    public static string Snapshot(string directory, string name, string text)
    {
        var version = VersionOf(text);
        var file = VersionFileOf(directory, name, version);
        if (!File.Exists(file))
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, text, new System.Text.UTF8Encoding(false));
            File.Move(temporary, file, true);
        }
        return version;
    }

    /// <summary>The version of the model as it is saved now (kept, if it was saved before versions were).</summary>
    public static string CurrentVersion(string directory, string name) => Snapshot(directory, name, File.ReadAllText(FileOf(directory, name)));

    /// <summary>A kept text, or null when there is none under that version.</summary>
    public static string? TextOfVersion(string directory, string name, string version) =>
        Regex.IsMatch(version, "^[0-9a-f]{64}$") && File.Exists(VersionFileOf(directory, name, version)) ? File.ReadAllText(VersionFileOf(directory, name, version)) : null;

    public static IEnumerable<string> Names(string directory)
    {
        var folder = Path.Combine(directory, Folder);
        return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.yaml").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal) : [];
    }

    /// <summary>Loads a model's text as the file <c>&lt;name&gt;.yaml</c>; the model's name must be the file's.</summary>
    public static DataModelLoadResult Check(string name, string text)
    {
        var file = name + ".yaml";
        var result = DataModelLoader.Load(text, file);
        if (result.Model is { } model && model.Name != name)
        {
            var diagnostics = result.Diagnostics.Append(new ConfigDiagnostic(DataModelLoader.BadName, ConfigSeverity.Error, file, 1, 1,
                $"The model is called '{model.Name}' but its file is {file}; a model's file is named for it.", $"Rename the file or set name: {name}.")).ToList();
            return new DataModelLoadResult(model, diagnostics);
        }
        return result;
    }

    private static JsonArray Diagnostics(IEnumerable<ConfigDiagnostic> diagnostics) => new(diagnostics.Select(d => (JsonNode)new JsonObject
    {
        ["code"] = d.Code,
        ["severity"] = d.Severity.ToString().ToLowerInvariant(),
        ["file"] = d.Path,
        ["line"] = d.Line,
        ["column"] = d.Column,
        ["message"] = d.Message,
        ["fix"] = d.Fix,
    }).ToArray());

    internal static JsonObject Schema(DataModel model) => ModelSchemaJson.Of(model);

    public static string NameProblem(string name) => $"'{name}' is not a model name: lower-case letters, digits, '-' and '_', starting with a letter or digit, at most 64.";
}
