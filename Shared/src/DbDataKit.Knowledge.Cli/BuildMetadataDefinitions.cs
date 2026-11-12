using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;
using DbDataKit.State;

namespace DbDataKit.Knowledge.Cli;

/// <summary>
/// The kind <c>build-metadata</c> in plan 30's registry (plan 27a KN13): what Build knows about the models of a project on a connection (the metadata documents it already writes: title, description, tags, properties, model kind, columns and types, what each reads), published to the serving
/// group so the <c>knowledge</c> module can render an item per model and per column (<see cref="GeneratedBuildKnowledge"/>). A definition is named <c>project.connection</c>; a version is the manifest <c>build-metadata.yml</c> (<c>build-metadata: 1</c>, <c>project</c>, <c>connection</c>, optionally <c>owner</c>) and the one
/// file <c>metadata.json</c> with the documents. It is data and nothing runs from it: it serves at once on a serving target, and a version that drops a model drops that model's items when it is served.
/// </summary>
public static class BuildMetadataDefinitions
{
    public const string Kind = "build-metadata";
    public const int Version = 1;
    public const string ManifestFile = "build-metadata.yml";
    public const string DocumentFile = "metadata.json";

    public static DefinitionKinds Register(DefinitionKinds kinds)
    {
        kinds.Register(new DefinitionKind(Kind, ["serving"], PublishMode.Serve)
        {
            Formats = new DefinitionFormats(Version, Version),
            FormatOf = text => FormatNumber(text),
            Validator = Validate,
        });
        return kinds;
    }

    public static DefinitionKinds Registry() => Register(new DefinitionKinds());

    private static int? FormatNumber(string manifest)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        return StrictYamlReader.Read(manifest.TrimStart('﻿'), ManifestFile, diagnostics) is YamlMapping map && map.Get(Kind) is YamlScalar { Quoted: false } s
            && int.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 ? number : null;
    }

    public static string Manifest(string project, string connection, string? owner = null) =>
        $"{Kind}: {Version}\nproject: {project}\nconnection: {connection}\n" + (owner is null ? "" : $"owner: {owner}\n");

    /// <summary>What a manifest says.</summary>
    public static (string? Project, string? Connection, string? Owner, IReadOnlyList<ConfigDiagnostic> Diagnostics) ReadManifest(string manifest)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        if (StrictYamlReader.Read(manifest.TrimStart('﻿'), ManifestFile, diagnostics) is not YamlMapping map)
        {
            if (diagnostics.Count == 0) diagnostics.Add(new ConfigDiagnostic(SharedCodes.InvalidValue, ConfigSeverity.Error, ManifestFile, 1, 1, "The manifest is empty."));
            return (null, null, null, diagnostics);
        }
        string? project = null, connection = null, owner = null;
        foreach (var entry in map.Entries)
            switch (entry.Key.Value)
            {
                case Kind: break;
                case "project": project = (entry.Value as YamlScalar)?.Value; break;
                case "connection": connection = (entry.Value as YamlScalar)?.Value; break;
                case "owner": owner = (entry.Value as YamlScalar)?.Value; break;
                default: diagnostics.Add(new ConfigDiagnostic(SharedCodes.UnknownKey, ConfigSeverity.Error, ManifestFile, entry.Key.Line, entry.Key.Column, $"'{entry.Key.Value}' is not a key of {Kind}/{Version}; the keys are {Kind}, project, connection and owner.")); break;
            }
        if (string.IsNullOrWhiteSpace(project)) diagnostics.Add(new ConfigDiagnostic(SharedCodes.MissingKey, ConfigSeverity.Error, ManifestFile, 1, 1, "The manifest has no `project`."));
        if (string.IsNullOrWhiteSpace(connection)) diagnostics.Add(new ConfigDiagnostic(SharedCodes.MissingKey, ConfigSeverity.Error, ManifestFile, 1, 1, "The manifest has no `connection`."));
        return (project, connection, owner, diagnostics);
    }

    private static string? Validate(DefinitionCandidate candidate)
    {
        var (project, connection, _, diagnostics) = ReadManifest(candidate.Manifest);
        var problems = diagnostics.Where(d => d.Severity == ConfigSeverity.Error).Select(d => d.ToString()).ToList();
        if (project is not null && connection is not null && !string.Equals(GeneratedBuildKnowledge.DefinitionName(project, connection), candidate.Name, StringComparison.Ordinal))
            problems.Add($"The manifest says project `{project}` on connection `{connection}`, which is published as `{GeneratedBuildKnowledge.DefinitionName(project, connection)}`, not `{candidate.Name}`.");
        if (candidate.Artifacts.Count != 1 || candidate.Artifacts[0].Path != DocumentFile) problems.Add($"A version of {Kind} has one file, `{DocumentFile}`.");
        return problems.Count == 0 ? null : string.Join(" | ", problems);
    }

    /// <summary>Publishes the documents Build wrote for a project on a connection. The same documents again change nothing.</summary>
    public static PublishResult Publish(DefinitionPublisher publisher, PublishArtifacts artifacts, string project, string connection, JsonNode models, string by, string channel = DefinitionStore.Production, string? owner = null, string? note = null)
    {
        var document = new JsonObject { ["project"] = project, ["connection"] = connection, ["models"] = models.DeepClone() };
        var file = artifacts.Add(DocumentFile, Encoding.UTF8.GetBytes(document.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true })), "application/json");
        return publisher.Publish(new PublishRequest(Kind, GeneratedBuildKnowledge.DefinitionName(project, connection), Manifest(project, connection, owner), by, channel, null, null, note, [file]));
    }

    /// <summary>The document of a stored version.</summary>
    public static JsonNode Read(DefinitionPublisher publisher, PayloadStore payloads, string name, int version)
    {
        var artifact = publisher.Artifacts(Kind, name, version).Single(a => a.Path == DocumentFile);
        using var stream = payloads.OpenRead(artifact.PayloadId);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return JsonNode.Parse(reader.ReadToEnd()) ?? throw new InvalidOperationException($"The version {version} of {Kind} {name} has no document.");
    }
}
