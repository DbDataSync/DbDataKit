using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DbData.Dialect;
using YamlDotNet.RepresentationModel;

namespace DbData.Metadata;

/// <summary>One connection's committed dialect: what the server reported and the profile it was mapped to.</summary>
public sealed record DialectLockEntry(string Connection, ServerFacts Reported, DialectProfile Profile)
{
    public static DialectLockEntry From(string connection, ServerFacts facts, DialectCatalog? catalog = null) =>
        new(connection, facts, DialectProfiles.ToProfile(facts, catalog));
}

public sealed class DialectLockException(string message) : FormatException(message);

/// <summary>
/// A <c>dialects.lock.yml</c>-style file: deterministic text (sorted connections, LF line ends, no timestamps, nothing about the machine)
/// that records each connection's <see cref="ServerFacts"/> and <see cref="DialectProfile"/>. Parsing is strict: unknown or missing keys,
/// values outside the choices, and a <c>profile_hash</c> that is not the hash of the profile beside it (a hand edit) are all errors.
/// </summary>
public static partial class DialectLockFile
{
    public const int Format = 1;

    private static readonly string[] TopKeys = ["format", "connections"];
    private static readonly string[] EntryKeys = ["reported", "dialect", "profile", "profile_hash"];
    private static readonly string[] ReportedKeys =
    [
        "product", "product_version", "driver", "quote_open", "quote_close", "stored_case", "quoted_case", "max_identifier_length", "catalog_separator",
        "catalog_term", "schema_term", "read_only", "supports_top", "supports_limit", "supports_offset_fetch", "supports_multiple_statements",
    ];
    private static readonly string[] ProfileKeys = ["quote", "parameters", "identifier_case", "row_limit", "max_identifier_length", "multi_statement"];

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_.-]*$")]
    private static partial Regex PlainScalar();

    public static string Render(IEnumerable<DialectLockEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("# Written by the metadata tooling. Commit it: later runs read it and do not ask the server.\n");
        sb.Append("# Do not edit; regenerate instead (a changed profile no longer matches its profile_hash).\n");
        sb.Append("format: ").Append(Format).Append('\n');
        var list = entries.OrderBy(e => e.Connection, StringComparer.Ordinal).ToList();
        if (list.Count == 0) { sb.Append("connections: {}\n"); return sb.ToString(); }
        sb.Append("connections:\n");
        foreach (var e in list)
        {
            sb.Append("  ").Append(Scalar(e.Connection)).Append(":\n");
            var f = e.Reported;
            sb.Append("    reported:\n");
            Add(sb, "product", f.ProductName); Add(sb, "product_version", f.ProductVersion); Add(sb, "driver", f.DriverName);
            Add(sb, "quote_open", f.QuoteOpen); Add(sb, "quote_close", f.QuoteClose);
            Add(sb, "stored_case", f.StoredCase?.ToString().ToLowerInvariant()); Add(sb, "quoted_case", f.QuotedCase?.ToString().ToLowerInvariant());
            Add(sb, "max_identifier_length", f.MaxIdentifierLength?.ToString(CultureInfo.InvariantCulture), raw: true);
            Add(sb, "catalog_separator", f.CatalogSeparator); Add(sb, "catalog_term", f.CatalogTerm); Add(sb, "schema_term", f.SchemaTerm);
            Add(sb, "read_only", Flag(f.ReadOnly), raw: true); Add(sb, "supports_top", Flag(f.SupportsTop), raw: true);
            Add(sb, "supports_limit", Flag(f.SupportsLimit), raw: true); Add(sb, "supports_offset_fetch", Flag(f.SupportsOffsetFetch), raw: true);
            Add(sb, "supports_multiple_statements", Flag(f.SupportsMultipleStatements), raw: true);
            var p = e.Profile;
            sb.Append("    dialect: ").Append(Scalar(p.Dialect)).Append('\n');
            sb.Append("    profile:\n");
            sb.Append("      quote: ").Append(Scalar(p.Quote)).Append('\n');
            sb.Append("      parameters: ").Append(Scalar(p.Parameters)).Append('\n');
            sb.Append("      identifier_case: ").Append(Scalar(p.IdentifierCase)).Append('\n');
            sb.Append("      row_limit: ").Append(Scalar(p.RowLimit)).Append('\n');
            sb.Append("      max_identifier_length: ").Append(p.MaxIdentifierLength.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("      multi_statement: ").Append(Flag(p.MultiStatement)).Append('\n');
            sb.Append("    profile_hash: ").Append(Scalar(p.Hash)).Append('\n');
        }
        return sb.ToString();
    }

    private static string? Flag(bool? value) => value is null ? null : value.Value ? "true" : "false";

    private static void Add(StringBuilder sb, string key, string? value, bool raw = false)
    {
        if (value is null) return;
        sb.Append("      ").Append(key).Append(": ").Append(raw ? value : Scalar(value)).Append('\n');
    }

    /// <summary>A YAML scalar that reads back as the same string: plain for simple words, else double-quoted with escapes.</summary>
    internal static string Scalar(string value)
    {
        if (PlainScalar().IsMatch(value) && !value.ToLowerInvariant().Is("true", "false", "null", "yes", "no", "on", "off", "y", "n")) return value;
        var sb = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    private static bool Is(this string value, params string[] options) => options.Contains(value, StringComparer.Ordinal);

    /// <exception cref="DialectLockException">The file is damaged, hand-edited or of another format.</exception>
    public static IReadOnlyList<DialectLockEntry> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var stream = new YamlStream();
        try { stream.Load(new StringReader(text)); }
        catch (YamlDotNet.Core.YamlException ex) { throw new DialectLockException("not valid YAML: " + ex.Message); }
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root) throw new DialectLockException("the file is not a mapping");

        var top = Keys(root, TopKeys, TopKeys, "the file");
        if (Text(top["format"], "format") != Format.ToString(CultureInfo.InvariantCulture)) throw new DialectLockException($"unsupported format (expected {Format})");
        if (top["connections"] is not YamlMappingNode connections) throw new DialectLockException("'connections' must be a mapping");

        var entries = new List<DialectLockEntry>();
        foreach (var (keyNode, valueNode) in connections.Children)
        {
            string name = Text(keyNode, "connection name");
            if (valueNode is not YamlMappingNode entryNode) throw new DialectLockException($"connection '{name}' must be a mapping");
            var e = Keys(entryNode, EntryKeys, EntryKeys, $"connection '{name}'");
            var reported = ParseReported(e["reported"], name);
            string dialect = Text(e["dialect"], "dialect");
            var p = Keys(e["profile"] as YamlMappingNode ?? throw new DialectLockException($"'{name}': profile must be a mapping"), ProfileKeys, ProfileKeys, $"profile of '{name}'");
            var profile = new DialectProfile(dialect, Text(p["quote"], "quote"), Text(p["parameters"], "parameters"), Text(p["identifier_case"], "identifier_case"),
                Text(p["row_limit"], "row_limit"), Int(p["max_identifier_length"], "max_identifier_length"), Bool(p["multi_statement"], "multi_statement"));
            if (Text(e["profile_hash"], "profile_hash") != profile.Hash)
                throw new DialectLockException($"'{name}': profile_hash does not match the profile (edited by hand?)");
            entries.Add(new DialectLockEntry(name, reported, profile));
        }
        return entries;
    }

    private static ServerFacts ParseReported(YamlNode node, string connection)
    {
        if (node is not YamlMappingNode map) throw new DialectLockException($"'{connection}': reported must be a mapping");
        var r = Keys(map, [], ReportedKeys, $"reported of '{connection}'");
        string? S(string k) => r.TryGetValue(k, out var n) ? Text(n, k) : null;
        int? I(string k) => r.TryGetValue(k, out var n) ? Int(n, k) : null;
        bool? B(string k) => r.TryGetValue(k, out var n) ? Bool(n, k) : null;
        IdentifierCase? C(string k) => S(k) is { } s ? Enum.TryParse<IdentifierCase>(s, ignoreCase: true, out var c) ? c : throw new DialectLockException($"{k}: unknown case '{s}'") : null;
        return new ServerFacts(S("product"), S("product_version"), S("driver"), S("quote_open"), S("quote_close"), C("stored_case"), C("quoted_case"),
            I("max_identifier_length"), S("catalog_separator"), S("catalog_term"), S("schema_term"), B("read_only"),
            B("supports_top"), B("supports_limit"), B("supports_offset_fetch"), B("supports_multiple_statements"));
    }

    // Reads a mapping whose keys must include every required key and only allowed keys; duplicates are errors.
    private static Dictionary<string, YamlNode> Keys(YamlMappingNode map, string[] required, string[] allowed, string where)
    {
        var result = new Dictionary<string, YamlNode>(StringComparer.Ordinal);
        foreach (var (k, v) in map.Children)
        {
            string key = Text(k, "key");
            if (!allowed.Contains(key)) throw new DialectLockException($"{where}: unknown key '{key}'");
            if (!result.TryAdd(key, v)) throw new DialectLockException($"{where}: duplicate key '{key}'");
        }
        foreach (string key in required)
            if (!result.ContainsKey(key)) throw new DialectLockException($"{where}: missing key '{key}'");
        return result;
    }

    private static string Text(YamlNode node, string what) =>
        node is YamlScalarNode { Value: { } v } ? v : throw new DialectLockException($"{what} must be a scalar");

    private static int Int(YamlNode node, string what) =>
        int.TryParse(Text(node, what), NumberStyles.None, CultureInfo.InvariantCulture, out int v) ? v : throw new DialectLockException($"{what} must be a number");

    private static bool Bool(YamlNode node, string what) => Text(node, what) switch
    {
        "true" => true,
        "false" => false,
        _ => throw new DialectLockException($"{what} must be true or false"),
    };
}
