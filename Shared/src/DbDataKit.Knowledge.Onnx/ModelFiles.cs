using System.Globalization;
using DbDataKit.Config.Yaml;
using DbDataKit.State;

namespace DbDataKit.Knowledge.Onnx;

/// <summary>
/// The kind <c>embedding-model</c> in plan 30's registry (plan 27a KN06): the files of a built-in model kept as a published artifact, so an install that cannot reach the internet gets them from its own state (the payload store, each file by hash) and a runner that needs the model is sent them like any
/// package. A definition is named by the model's id without <c>builtin/</c>; a version is the manifest <c>embedding-model.yml</c> (<c>embedding-model: 1</c>, <c>id</c>) and exactly the files the catalog lists for the model, each with the hash the catalog records: <b>a publish of any other file is refused</b>, so a
/// substituted model never reaches a channel. Data, not code: it serves at once.
/// </summary>
public static class ModelFiles
{
    public const string Kind = "embedding-model";
    public const int Version = 1;
    public const string ManifestFile = "embedding-model.yml";

    public static DefinitionKinds Register(DefinitionKinds kinds)
    {
        kinds.Register(new DefinitionKind(Kind, ["serving", "runner"], PublishMode.Serve)
        {
            Formats = new DefinitionFormats(Version, Version),
            FormatOf = ManifestFormat,
            Validator = Validate,
        });
        return kinds;
    }

    public static DefinitionKinds Registry() => Register(new DefinitionKinds());

    /// <summary>The name a model is published under: its id without the <c>builtin/</c> that says where it runs.</summary>
    public static string NameOf(BuiltInModel model) => model.Id[(model.Id.IndexOf('/', StringComparison.Ordinal) + 1)..];

    public static string Manifest(BuiltInModel model) => $"{Kind}: {Version}\nid: {model.Id}\n";

    private static int? ManifestFormat(string manifest)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        return StrictYamlReader.Read(manifest.TrimStart('﻿'), ManifestFile, diagnostics) is YamlMapping map && map.Get(Kind) is YamlScalar { Quoted: false } s
            && int.TryParse(s.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 ? number : null;
    }

    private static string? Validate(DefinitionCandidate candidate)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        if (StrictYamlReader.Read(candidate.Manifest.TrimStart('﻿'), ManifestFile, diagnostics) is not YamlMapping map) return "The manifest is empty or does not read.";
        var id = (map.Get("id") as YamlScalar)?.Value;
        var unknown = map.Entries.Select(e => e.Key.Value).FirstOrDefault(k => k is not (Kind or "id"));
        if (unknown is not null) return $"'{unknown}' is not a key of {Kind}/{Version}; the keys are {Kind} and id.";
        if (id is null || BuiltInEmbeddingModels.Find(id) is not { } model)
            return $"The model '{id}' is not one this product offers (a model whose licence is not recorded is not offered). It offers: {string.Join(", ", BuiltInEmbeddingModels.Offered.Select(m => m.Id))}.";
        if (!string.Equals(NameOf(model), candidate.Name, StringComparison.Ordinal)) return $"The manifest says the model is `{model.Id}`, which is published as `{NameOf(model)}`, not `{candidate.Name}`.";
        var problems = new List<string>();
        foreach (var file in model.Files)
            if (candidate.Artifacts.FirstOrDefault(a => a.Path == file.Name) is not { } artifact) problems.Add($"{file.Name} is not among the files of this version.");
            else if (!string.Equals(artifact.Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase)) problems.Add($"{file.Name} is not the file of {model.Id} (its SHA-256 is {artifact.Sha256}, not {file.Sha256}).");
        foreach (var extra in candidate.Artifacts.Where(a => model.Files.All(f => f.Name != a.Path))) problems.Add($"{extra.Path} is not a file of {model.Id}.");
        return problems.Count == 0 ? null : string.Join(" | ", problems);
    }

    /// <summary>Publishes the files of a model from a folder that holds it (checked first, so a damaged folder is refused naming the file). The same files again change nothing.</summary>
    public static PublishResult Publish(DefinitionPublisher publisher, PublishArtifacts artifacts, string folder, BuiltInModel model, string by, string channel = DefinitionStore.Production)
    {
        if (BuiltInEmbeddingModels.Verify(folder, model) is { Count: > 0 } problems) throw new InvalidDataException(string.Join(" ", problems));
        var refs = model.Files.Select(f => artifacts.AddFile(f.Name, Path.Combine(folder, f.Name), f.Name.EndsWith(".onnx", StringComparison.Ordinal) ? "application/octet-stream" : "text/plain")).ToList();
        return publisher.Publish(new PublishRequest(Kind, NameOf(model), Manifest(model), by, channel, null, null, null, refs));
    }

    /// <summary>
    /// Writes the files of a published version into a folder from the payload store (each file's hash is checked as it is read to its end) and checks the folder holds the model. How an offline install, or a runner, gets the model from its own state.
    /// </summary>
    public static void Restore(DefinitionPublisher publisher, PayloadStore payloads, BuiltInModel model, int version, string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var artifact in publisher.Artifacts(Kind, NameOf(model), version))
        {
            var target = Path.Combine(folder, artifact.Path);
            var temporary = target + "." + Guid.NewGuid().ToString("N")[..8] + ".part";
            try
            {
                using (var source = payloads.OpenRead(artifact.PayloadId))
                using (var destination = File.Create(temporary)) source.CopyTo(destination);
                File.Move(temporary, target, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        if (BuiltInEmbeddingModels.Verify(folder, model) is { Count: > 0 } problems) throw new InvalidDataException(string.Join(" ", problems));
    }
}
