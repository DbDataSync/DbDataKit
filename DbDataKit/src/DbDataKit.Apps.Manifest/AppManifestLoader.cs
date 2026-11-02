using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps;

public sealed class AppManifestLoadResult(AppManifest? manifest, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public AppManifest? Manifest { get; } = manifest;
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
    public bool Ok => Manifest is not null && !Diagnostics.HasErrors();
}

/// <summary>Strict reader and validator of <c>app/1</c> manifests. A problem is a diagnostic with the file, line and column; nothing here throws on a damaged file.</summary>
public static class AppManifestLoader
{
    public const string BadName = "DBD-APP-001";
    public const string BadVersion = "DBD-APP-002";
    public const string BadPath = "DBD-APP-003";
    public const string NothingToServe = "DBD-APP-004";
    public const string BadUse = "DBD-APP-005";
    public const string MissingFile = "DBD-APP-006";
    public const string AbsoluteAsset = "DBD-APP-007";

    public const int Version = 1;

    /// <summary>The name rule instances, subdomains and the Store's models share (plan 26 section 3).</summary>
    public static readonly Regex NameRule = new("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Names an instance may not take: they are the host's own addresses.</summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string> { "api", "admin", "console", "identity", "apps", "static", "health", "version" };

    public static AppManifestLoadResult Load(string yaml, string file = "app.yaml")
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, file, diags);
        if (root is null || diags.HasErrors()) return new(null, diags);
        if (root is not YamlMapping map)
        {
            diags.Add(DefaultDiagnosticCatalog.Instance.Create(SharedCodes.InvalidValue, file, root.Line, root.Column, "An app manifest is a mapping with `schemaVersion`, `name` and `version`."));
            return new(null, diags);
        }
        var manifest = new Reader(file, diags).Read(map);
        return new(diags.HasErrors() ? null : manifest, diags);
    }

    private sealed class Reader(string file, List<ConfigDiagnostic> diags) : YamlFieldReader(file, diags)
    {
        private static readonly string[] RootKeys = ["schemaVersion", "name", "title", "description", "version", "entry", "views", "origin", "chrome", "models", "semantic", "roles"];
        private static readonly Regex SemVer = new(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex Identifier = new("^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public AppManifest? Read(YamlMapping map)
        {
            CheckKeys(map, RootKeys, "the manifest");
            if (!CheckVersion(map)) return null;

            var name = Name(map);
            var version = Scalar(map, "version", required: true, map);
            if (version is not null && !SemVer.IsMatch(version.Value)) { Add(BadVersion, version, $"`{version.Value}` is not a version: use three numbers, such as 1.2.0."); version = null; }

            var entry = PathOf(map, "entry");
            var views = new List<string>();
            if (map.Get("views") is { } viewsNode)
            {
                if (viewsNode is not YamlSequence list) Add(SharedCodes.InvalidValue, viewsNode, "`views` must be a list of file paths.");
                else foreach (var item in list.Items) if (item is YamlScalar s && SafePath(s) is { } p) views.Add(p); else if (item is not YamlScalar) Add(SharedCodes.InvalidValue, item, "A view is a file path.");
            }
            if (entry is null && views.Count == 0 && map.Get("entry") is null)
                Add(NothingToServe, map, "The app has nothing to serve: give it an `entry` (code) or `views` (declarative specs).", "Add `entry: src/main.ts` or `views: [views/home.yaml]`.");

            var origin = map.Get("origin") is YamlScalar o ? Choice(o, "origin", new Dictionary<string, AppOrigin> { ["sandbox"] = AppOrigin.Sandbox, ["own"] = AppOrigin.Own }) : AppOrigin.Sandbox;
            var chrome = map.Get("chrome") is YamlScalar c ? Choice(c, "chrome", new Dictionary<string, AppChrome> { ["shell"] = AppChrome.Shell, ["none"] = AppChrome.None }) : AppChrome.Shell;
            var models = Models(map);
            var semantic = Names(map, "semantic");
            var roles = Names(map, "roles");
            var title = map.Get("title") is YamlScalar t ? t.Value : null;
            var description = map.Get("description") is YamlScalar d ? d.Value : null;
            if (name is null || version is null) return null;
            return new AppManifest(name, title, description, version.Value, entry, views, origin, chrome, models, semantic, roles);
        }

        private bool CheckVersion(YamlMapping map)
        {
            var node = map.Get("schemaVersion");
            if (node is null) { Add(SharedCodes.SchemaVersionInvalid, map, "`schemaVersion` is missing.", "Add `schemaVersion: 1`."); return false; }
            if (node is not YamlScalar { Value: var text } || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
            { Add(SharedCodes.SchemaVersionInvalid, node, "`schemaVersion` must be a positive whole number."); return false; }
            if (version > AppManifestLoader.Version)
            { Add(SharedCodes.SchemaVersionNewer, node, $"`schemaVersion` is {version}, but this tool understands up to {AppManifestLoader.Version}.", "Upgrade the tool; this file was written by a newer version."); return false; }
            return true;
        }

        private string? Name(YamlMapping map)
        {
            var scalar = Scalar(map, "name", required: true, map);
            if (scalar is null) return null;
            if (!NameRule.IsMatch(scalar.Value)) { Add(BadName, scalar, $"`{scalar.Value}` is not an app name: lower-case letters, digits, '-' and '_', starting with a letter or digit, at most 64."); return null; }
            if (Reserved.Contains(scalar.Value)) { Add(BadName, scalar, $"`{scalar.Value}` is an address the host keeps for itself; choose another name."); return null; }
            return scalar.Value;
        }

        private string? PathOf(YamlMapping map, string key) => map.Get(key) is YamlScalar s ? SafePath(s) : map.Get(key) is { } other ? Invalid(other, key) : null;

        private string? Invalid(YamlNode node, string key) { Add(SharedCodes.InvalidValue, node, $"`{key}` must be a file path."); return null; }

        /// <summary>A path inside the app's folder: relative, forward slashes, no way out.</summary>
        private string? SafePath(YamlScalar s)
        {
            var path = s.Value;
            if (path.Length == 0 || path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':') || path.Contains('\\') || path.Split('/').Any(part => part is ".." or ""))
            { Add(BadPath, s, $"`{path}` is not a path inside the app: use a relative path with forward slashes and no `..`."); return null; }
            return path;
        }

        private T Choice<T>(YamlScalar at, string what, Dictionary<string, T> options) where T : struct
        {
            if (options.TryGetValue(at.Value, out var value)) return value;
            Add(SharedCodes.InvalidValue, at, $"`{at.Value}` is not a value of `{what}`.", $"Use one of: {string.Join(", ", options.Keys)}.");
            return default;
        }

        private List<string> Names(YamlMapping map, string key)
        {
            var found = new List<string>();
            if (map.Get(key) is not { } node) return found;
            if (node is not YamlSequence list) { Add(SharedCodes.InvalidValue, node, $"`{key}` must be a list of names."); return found; }
            foreach (var item in list.Items)
            {
                if (item is not YamlScalar s || !NameRule.IsMatch(s.Value)) { Add(BadName, item, $"`{(item as YamlScalar)?.Value}` is not a name in `{key}`: lower-case letters, digits, '-' and '_'."); continue; }
                if (found.Contains(s.Value)) { Add(BadName, s, $"`{s.Value}` is listed twice in `{key}`."); continue; }
                found.Add(s.Value);
            }
            return found;
        }

        private List<AppModelUse> Models(YamlMapping map)
        {
            var uses = new List<AppModelUse>();
            if (map.Get("models") is not { } node) return uses;
            if (node is not YamlSequence list) { Add(SharedCodes.InvalidValue, node, "`models` must be a list: `- model: territories` with its `entities`."); return uses; }
            foreach (var item in list.Items)
            {
                if (item is not YamlMapping use) { Add(SharedCodes.InvalidValue, item, "Each entry of `models` is a mapping with `model` and `entities`."); continue; }
                CheckKeys(use, ["model", "entities"], "a `models` entry");
                var model = Scalar(use, "model", required: true, use);
                var entities = new Dictionary<string, DataOperations>();
                if (use.Get("entities") is not YamlMapping entityMap || entityMap.Entries.Count == 0)
                    Add(BadUse, (YamlNode?)use.Get("entities") ?? use, "`entities` names each entity the app uses and what it does: `territory: [read, write]`.");
                else
                    foreach (var entry in entityMap.Entries)
                    {
                        if (!Identifier.IsMatch(entry.Key.Value)) { Add(BadUse, entry.Key, $"`{entry.Key.Value}` is not an entity name."); continue; }
                        if (entry.Value is not YamlSequence ops || ops.Items.Count == 0) { Add(BadUse, entry.Value, $"`{entry.Key.Value}` needs a list of operations: read, write."); continue; }
                        var allowed = DataOperations.None;
                        foreach (var op in ops.Items)
                        {
                            if (op is YamlScalar { Value: "read" }) allowed |= DataOperations.Read;
                            else if (op is YamlScalar { Value: "write" }) allowed |= DataOperations.Write;
                            else Add(BadUse, op, $"`{(op as YamlScalar)?.Value}` is not an operation: use read or write.");
                        }
                        // writing a row is reading it first, so write implies read
                        entities[entry.Key.Value] = allowed.HasFlag(DataOperations.Write) ? allowed | DataOperations.Read : allowed;
                    }
                if (model is not null) uses.Add(new AppModelUse(model.Value, entities));
            }
            return uses;
        }
    }
}
