using System.Globalization;
using System.Text;
using DbDataKit.Config.Yaml;
using DbDataKit.State;

namespace DbDataKit.Knowledge.Cli;

/// <summary>A refusal to publish a knowledge project: every diagnostic that stopped it, each with file and line (never a thrown exception for a bad item).</summary>
public sealed class KnowledgePublishRefusedException(IReadOnlyList<ConfigDiagnostic> problems) : Exception(string.Join(" | ", problems.Where(p => p.Severity == ConfigSeverity.Error).Select(p => p.ToString())))
{
    public IReadOnlyList<ConfigDiagnostic> Problems { get; } = problems;
}

/// <summary>
/// The kind <c>knowledge-project</c> in plan 30's registry (plan 27a KN10): one version per publish, each Markdown file and each <c>_knowledge.yml</c> an artifact kept by its hash, published to a channel. The manifest is <c>knowledge-project.yml</c>
/// (<c>knowledge-project: 1</c>, <c>name</c>, and optionally <c>source</c>: <c>install</c> or <c>project</c>, the name agents give it in <c>knowledge.from</c>). What a publish checks (the items, the subjects, the owners and the budgets of the agents each item reaches) is
/// <see cref="KnowledgeChecks"/>, run by <see cref="Publish"/> before anything is stored; the registry's validator checks what it can see (the manifest and the paths of the files).
/// </summary>
public static class KnowledgeDefinitions
{
    public const string Kind = "knowledge-project";
    public const int Version = 1;
    public const string ManifestFile = "knowledge-project.yml";

    /// <summary>The kind registered in a registry of its own.</summary>
    public static DefinitionKinds Registry() => Register(new DefinitionKinds());

    /// <summary>Registers the kind: a serving kind (the <c>knowledge</c> module of the serving layer follows it), staged by default so the module checks and embeds before the channel switches at activation.</summary>
    public static DefinitionKinds Register(DefinitionKinds kinds)
    {
        kinds.Register(new DefinitionKind(Kind, ["serving"], PublishMode.Stage)
        {
            Formats = new DefinitionFormats(Version, Version),
            FormatOf = text => ManifestFormat(text),
            Validator = Validate,
        });
        return kinds;
    }

    private static int? ManifestFormat(string manifest)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        return StrictYamlReader.Read(manifest.TrimStart('﻿'), ManifestFile, diagnostics) is YamlMapping map && map.Get(Kind) is YamlScalar { Quoted: false } s
            && int.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 ? number : null;
    }

    /// <summary>The manifest of a project, as text.</summary>
    public static string Manifest(string name, string? source = null) => $"{Kind}: {Version}\nname: {name}\n" + (source is null ? "" : $"source: {source}\n");

    /// <summary>What a manifest says: the name and the source agents know it by.</summary>
    public static (string? Name, string? Source, IReadOnlyList<ConfigDiagnostic> Diagnostics) ReadManifest(string manifest)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        if (StrictYamlReader.Read(manifest.TrimStart('﻿'), ManifestFile, diagnostics) is not YamlMapping map)
        {
            if (diagnostics.Count == 0) diagnostics.Add(new ConfigDiagnostic(SharedCodes.InvalidValue, ConfigSeverity.Error, ManifestFile, 1, 1, "The manifest is empty.", $"Write `{Kind}: {Version}` and `name:`."));
            return (null, null, diagnostics);
        }
        string? name = null, source = null;
        foreach (var entry in map.Entries)
        {
            var (key, value) = (entry.Key, entry.Value);
            switch (key.Value)
            {
                case Kind: break;
                case "name": name = (value as YamlScalar)?.Value; break;
                case "source": source = (value as YamlScalar)?.Value; break;
                default: diagnostics.Add(new ConfigDiagnostic(SharedCodes.UnknownKey, ConfigSeverity.Error, ManifestFile, key.Line, key.Column, $"'{key.Value}' is not a key of {Kind}/{Version}; the keys are {Kind}, name and source.")); break;
            }
        }
        if (string.IsNullOrWhiteSpace(name)) diagnostics.Add(new ConfigDiagnostic(SharedCodes.MissingKey, ConfigSeverity.Error, ManifestFile, 1, 1, "The manifest has no `name`.", "Write `name: <the project's name>`."));
        if (source is not null and not ("install" or "project")) diagnostics.Add(new ConfigDiagnostic(SharedCodes.InvalidValue, ConfigSeverity.Error, ManifestFile, 1, 1, $"source '{source}' is not install or project."));
        return (name, source, diagnostics);
    }

    private static string? Validate(DefinitionCandidate candidate)
    {
        var (name, _, diagnostics) = ReadManifest(candidate.Manifest);
        var problems = diagnostics.Where(d => d.Severity == ConfigSeverity.Error).Select(d => d.ToString()).ToList();
        if (name is not null && !string.Equals(name, candidate.Name, StringComparison.Ordinal)) problems.Add($"The manifest says the project's name is `{name}` and it is published as `{candidate.Name}`.");
        foreach (var file in candidate.Artifacts)
            if (!IsKnowledgeFile(file.Path)) problems.Add($"`{file.Path}` is not a file of a knowledge project: a Markdown file or `_knowledge.yml`, a relative path with no `..`.");
        if (candidate.Artifacts.Count == 0) problems.Add("The version has no files: a knowledge project has at least one Markdown file.");
        return problems.Count == 0 ? null : string.Join(" | ", problems);
    }

    /// <summary>Whether a path may be a file of a project (the reader's own rule: Markdown files and the folder defaults; nothing outside the folder).</summary>
    public static bool IsKnowledgeFile(string path) =>
        path.Length > 0 && !path.StartsWith('/') && !path.Contains('\\') && !path.Split('/').Any(p => p is ".." or "." or "")
        && (path.EndsWith(".md", StringComparison.Ordinal) || path.EndsWith("/" + KnowledgeItemLoader.FolderFileName, StringComparison.Ordinal) || path == KnowledgeItemLoader.FolderFileName);

    /// <summary>The files of a project folder as a version's artifacts: every Markdown file and folder defaults, a folder starting with <c>.</c> left out.</summary>
    public static IReadOnlyList<(string Path, string File)> FilesOf(string folder) =>
        [.. Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(f => (Path: System.IO.Path.GetRelativePath(folder, f).Replace('\\', '/'), File: f))
            .Where(f => !f.Path.Split('/').Any(p => p.StartsWith('.')) && IsKnowledgeFile(f.Path))
            .OrderBy(f => f.Path, StringComparer.Ordinal)];

    /// <summary>
    /// Checks a project folder and publishes it: one version, each file an artifact by hash. The checks are <see cref="KnowledgeChecks"/>; any error refuses the publish, naming file and line, and nothing is stored. Publishing the same files again changes nothing.
    /// </summary>
    /// <exception cref="KnowledgePublishRefusedException">The project has a bad item, a subject nobody knows, an owner that is not a group or an instruction that pushes an agent over its budget.</exception>
    public static PublishResult Publish(DefinitionPublisher publisher, PublishArtifacts artifacts, string folder, string name, string by, string channel, KnowledgeCheckReport report, string? source = null, PublishMode? mode = null, string? note = null)
    {
        if (!report.Ok) throw new KnowledgePublishRefusedException(report.Diagnostics);
        var refs = FilesOf(folder).Select(f => artifacts.Add(f.Path, File.ReadAllBytes(f.File), f.Path.EndsWith(".md", StringComparison.Ordinal) ? "text/markdown" : "application/yaml")).ToList();
        return publisher.Publish(new PublishRequest(Kind, name, Manifest(name, source), by, channel, mode, null, note, refs));
    }

    /// <summary>The text of every file of a stored version (for the module that follows the kind, and for a diff).</summary>
    public static IReadOnlyDictionary<string, string> Files(DefinitionPublisher publisher, PayloadStore payloads, string name, int version)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var artifact in publisher.Artifacts(Kind, name, version))
        {
            using var stream = payloads.OpenRead(artifact.PayloadId);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            files[artifact.Path] = reader.ReadToEnd();
        }
        return files;
    }
}
