using System.Text;
using System.Text.RegularExpressions;
using DbDataKit.Authoring;
using DbDataKit.DataModel;

namespace DbDataKit.Authoring.Server;

/// <summary>
/// The <c>store</c> kind (plan 33 SC21a): master data models, <c>model/1</c> and <c>model/2</c> files, authored in a git workspace, one file per model in <c>models/</c>. The shared library that the Store reads them with,
/// <see cref="DataModelLoader"/>, checks every file (the same file, line and column diagnostics the Store's editor shows), so the Workspace needs no copy of the Store and no child process. A model is published to a Gateway
/// (SC21b); this kind is checked, not built.
/// </summary>
public sealed partial class StoreProjectKind : IProjectKind
{
    public const string Folder = "models";

    public string Name => "store";
    public string Title => "Store models";
    public string Description => "Master data models (model files with entities, keys, references and rules) that the Store keeps validated rows of. Checked with the Store's own model reader; published to a Gateway that serves them.";
    public bool Buildable => false;
    public string? GitIgnore => null;

    public IReadOnlyList<ProjectPublishRoute> Publishes { get; } =
        [new("gateway", "model", "The models in the current format, published to a Gateway that serves them, on a data source the Gateway already has (the model's storage connection by default). The Workspace holds no connection and creates no table.")];

    public bool Produces => true;

    /// <summary>
    /// What a publish sends: each model of <c>models/</c> in the current format (<see cref="DataModelLoader.WithCurrentVersion"/>, so a file at the previous format is published as the current one and the draft's file is left as written), with the
    /// data source it names in <c>storage.connection</c>, as <c>models/&lt;name&gt;.yaml</c> of the artifact and <c>info</c> (<c>models</c>, <c>data_sources</c>: <c>name=connection</c>, comma separated).
    /// </summary>
    public Task<ProjectArtifact> ProduceAsync(string root, string projectId, string outputFolder, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(root, Folder);
        var names = new List<string>();
        var sources = new List<string>();
        Directory.CreateDirectory(Path.Combine(outputFolder, "models"));
        foreach (var file in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly).Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
        {
            var relative = Folder + "/" + Path.GetFileName(file);
            var text = File.ReadAllText(file, Utf8).ReplaceLineEndings("\n");
            var loaded = DataModelLoader.Load(text, relative);
            if (loaded.Model is not { } model)
                return Task.FromResult(new ProjectArtifact(false, $"{relative} is not a model that loads.", [new("error", "DBD-WS-STORE-LOAD", $"{relative} is not a model that loads.", relative)]));
            File.WriteAllText(Path.Combine(outputFolder, "models", model.Name + ".yaml"), DataModelLoader.WithCurrentVersion(text), Utf8);
            names.Add(model.Name);
            sources.Add(model.Name + "=" + model.Connection);
        }
        var info = new Dictionary<string, string> { ["models"] = string.Join(", ", names), ["data_sources"] = string.Join(", ", sources) };
        return Task.FromResult(new ProjectArtifact(true, $"{names.Count} model(s): {string.Join(", ", names)}.", [], info));
    }

    private static readonly UTF8Encoding Utf8 = new(false, true);

    [GeneratedRegex("[^A-Za-z0-9_]")]
    private static partial Regex NotIdentifier();

    /// <summary>The name a model of a project gets: the project's id as a plain identifier (a model's names are letters, digits and <c>_</c>, starting with a letter).</summary>
    public static string ModelName(string id)
    {
        var name = NotIdentifier().Replace(id, "_");
        return char.IsAsciiLetter(name[0]) ? name : "m_" + name;
    }

    public IReadOnlyList<ProjectKindFile> Template(string id, string? title)
    {
        var name = ModelName(id);
        var model = $$"""
            schemaVersion: {{DataModelLoader.Version}}
            name: {{name}}
            title: {{Quote(title ?? id)}}
            storage: {connection: mdm}
            approval: {changes: none}
            entities:
              territory:
                title: Territory
                key: [code]
                history: audit
                attributes:
                  code: {type: VARCHAR(10), required: true}
                  name: {type: VARCHAR(100), required: true}
                  active: {type: BOOLEAN, required: true, default: true}
            """.Replace("\r\n", "\n") + "\n";
        var readme = $"# {title ?? id}\n\nMaster data models of the Store. One file per model in `{Folder}/`; edit it in a workspace, check it, merge it, publish it.\n`storage.connection` names the connection the rows live in on the server that serves the model.\n";
        return [new($"{Folder}/{name}.yaml", Utf8.GetBytes(model)), new("README.md", Utf8.GetBytes(readme))];
    }

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public Task<IReadOnlyList<ProjectDiagnostic>> CheckAsync(string root, CancellationToken cancellationToken)
    {
        var found = new List<ProjectDiagnostic>();
        var folder = Path.Combine(root, Folder);
        var files = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly).Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList()
            : [];
        if (files.Count == 0) found.Add(new("error", "DBD-WS-STORE-EMPTY", $"This draft has no model: a Store project keeps one file per model in {Folder}/ (name.yaml)."));
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var relative = Folder + "/" + Path.GetFileName(file);
            string text;
            try { text = File.ReadAllText(file, Utf8); }
            catch (Exception ex) when (ex is IOException or DecoderFallbackException) { found.Add(new("error", "DBD-WS-STORE-READ", $"{relative} cannot be read: {ex.Message}", relative)); continue; }
            var loaded = DataModelLoader.Load(text, relative);
            foreach (var d in loaded.Diagnostics)
                found.Add(new(d.Severity switch { DbDataKit.Config.Yaml.ConfigSeverity.Error => "error", DbDataKit.Config.Yaml.ConfigSeverity.Warning => "warning", _ => "info" }, d.Code, d.Message, relative, d.Line > 0 ? d.Line : null));
            if (loaded.Model is { } model)
            {
                // two files of one project that declare one model would publish over each other
                if (names.TryGetValue(model.Name, out var first)) found.Add(new("error", "DBD-WS-STORE-DUPLICATE", $"The model '{model.Name}' is also declared in {first}; a model has one file.", relative));
                else names[model.Name] = relative;
            }
        }
        return Task.FromResult<IReadOnlyList<ProjectDiagnostic>>(found);
    }
}
