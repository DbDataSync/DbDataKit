using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

using DbDataKit.Connections;

namespace DbDataBuild.Models;

/// <summary>
/// The connections a project declares, as files: <c>connections/&lt;name&gt;.yaml</c>, each a <c>connection/1</c> document (plan 13, E03), the format the console, Sync and Proxy read. The shared loader checks the
/// document (unknown keys, a wrong <c>schema</c>, plaintext credentials); this class adds what is Build's: the file is named for the connection, it carries no address, login, TLS or timeout setting (the login is the
/// environment variable <c>DBDATA_BUILD_&lt;NAME&gt;_&lt;READ|WRITE&gt;</c>), <c>engine</c> is the driver alone (<c>access: readonly</c> and <c>dialect</c> say the rest) and everything of Build's own is under
/// <c>extensions.build</c>.
/// <para>
/// What comes out is the mapping that <see cref="ProjectConfigLoader"/> reads connections from (name to settings, the settings being <c>engine</c> as a keyword string and the keys of <c>extensions.build</c>), with each node
/// remembering the file it came from so a diagnostic names <c>connections/&lt;name&gt;.yaml</c> and the line in it, not the project file.
/// </para>
/// </summary>
public static class ProjectConnectionFiles
{
    public const string Folder = "connections";

    /// <summary>The keys of a file that Build reads. Any other key of `connection/1` is refused as not used by this tool; an unknown one is the shared loader's error.</summary>
    private static readonly string[] Used = ["schema", "name", "engine", "dialect", "access", "tls", "extensions"];

    /// <summary>The connections read from the files (null when there is no folder or no file) and, for every node, the file it was read from.</summary>
    public sealed record Loaded(YamlMapping? Connections, IReadOnlyDictionary<YamlNode, string> NodeFiles, IReadOnlyDictionary<string, TlsSpec> Tls);

    public static Loaded Read(string projectRoot, List<Diagnostic> diags)
    {
        var folder = Path.Combine(projectRoot, Folder);
        var files = new Dictionary<YamlNode, string>(ReferenceEqualityComparer.Instance);
        var tls = new Dictionary<string, TlsSpec>(StringComparer.Ordinal);
        if (!Directory.Exists(folder)) return new(null, files, tls);
        var entries = new List<YamlEntry>();
        foreach (var path in Directory.GetFiles(folder).OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = Folder + "/" + Path.GetFileName(path);
            if (!path.EndsWith(".yaml", StringComparison.Ordinal))
            {
                if (path.EndsWith(".yml", StringComparison.Ordinal)) Report(diags, relative, 0, 0, "A connection file ends in `.yaml`.", "Rename it to `" + Path.GetFileNameWithoutExtension(path) + ".yaml`.");
                continue;
            }
            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Report(diags, relative, 0, 0, "The file could not be read.", null); continue; }
            if (ReadOne(text, relative, Path.GetFileNameWithoutExtension(path), diags, files, tls) is { } entry) entries.Add(entry);
        }
        return new(entries.Count == 0 ? null : Own(new YamlMapping(entries, 1, 1), null, files), files, tls);
    }

    /// <summary>One file's text (for tests and for editors that hold the text): the entry the loader reads, or null with the problems in <paramref name="diags"/>.</summary>
    public static YamlEntry? ReadOne(string text, string relative, string expectedName, List<Diagnostic> diags, IDictionary<YamlNode, string> files, IDictionary<string, TlsSpec>? tls = null)
    {
        var before = diags.Count;
        var result = ConnectionDefinitionLoader.Load(text, relative, addressRequired: false);
        foreach (var d in result.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error))
            Report(diags, relative, d.Line, d.Column, d.Message, d.Fix);
        if (result.Definition is null || diags.Count > before) return null;

        var root = StrictYamlReader.Read(text, relative, diags) as YamlMapping;
        if (root is null || diags.Count > before) return null;
        void Bad(YamlNode at, string found, string? fix = null) => Report(diags, relative, at.Line, at.Column, found, fix);

        var definition = result.Definition;
        if (definition.Name != expectedName)
            Bad(root.Get("name") ?? root, $"The file is `{relative}` but `name` is `{definition.Name}`.", $"Name the file `{definition.Name}.yaml`, or set `name: {expectedName}`: the file name is the connection's name.");
        foreach (var e in root.Entries.Where(e => !Used.Contains(e.Key.Value)))
            Bad(e.Key, $"`{e.Key.Value}` is not used in a connection file of this tool.",
                "A project's connection holds no address, login or timeout setting: the login is the environment variable `" + $"DBDATA_BUILD_{LoginEnvironment.Sanitize(definition.Name)}_<READ|WRITE>`, and the tool's own settings are under `extensions.build`.");
        if (root.Get("engine") is YamlScalar engine && engine.Value.Any(char.IsWhiteSpace))
            Bad(engine, "`engine` is the driver alone.", "Write `access: readonly` for a read-only connection and `dialect: <name>` for the SQL dialect, each on its own line.");

        YamlMapping? build = null;
        if (root.Get("extensions") is YamlMapping extensions && extensions.Get("build") is { } own)
        {
            if (own is YamlMapping m) build = m;
            else Bad(own, "`extensions.build` must be a mapping of the tool's connection settings.");
        }
        if (build?.Get("engine") is { } dup) Bad(dup, "`engine` is a key of the file, not of `extensions.build`.");
        if (diags.Count > before) return null;

        // `readonly flightsql clickhouse`: the words the project file always spelled in `engine`
        var words = new List<string>();
        if (definition.Access == AccessMode.Readonly) words.Add("readonly");
        words.Add(definition.Engine);
        if (!string.IsNullOrEmpty(definition.Dialect)) words.Add(definition.Dialect);
        var at = root.Get("engine")!;
        var settings = new List<YamlEntry> { new(new YamlScalar("engine", at.Line, 1), new YamlScalar(string.Join(' ', words), at.Line, at.Column)) };
        settings.AddRange(build?.Entries ?? []);
        var nameAt = root.Get("name") ?? root;
        // how the login's connection is secured is the connection file's (encrypt, verify, trust_server_certificate, ca_file); a file with no `tls:` gets the shared default, which encrypts and verifies
        if (root.Get("tls") is not null && tls is not null) tls[definition.Name] = definition.Tls;
        var entry = new YamlEntry(new YamlScalar(definition.Name, nameAt.Line, nameAt.Column), new YamlMapping(settings, nameAt.Line, 1));
        Own(entry.Key, relative, files); Own(entry.Value, relative, files);
        return entry;
    }

    private static T Own<T>(T node, string? file, IDictionary<YamlNode, string> files) where T : YamlNode
    {
        if (file != null) Walk(node);
        return node;
        void Walk(YamlNode n)
        {
            files[n] = file!;
            switch (n)
            {
                case YamlMapping m: foreach (var e in m.Entries) { Walk(e.Key); Walk(e.Value); } break;
                case YamlSequence q: foreach (var i in q.Items) Walk(i); break;
            }
        }
    }

    private static void Report(List<Diagnostic> diags, string file, int line, int column, string found, string? fix) =>
        diags.Add(new Diagnostic(DiagnosticCatalog.ConnectionFileInvalid, new(file, line, column), found, null, fix));
}
