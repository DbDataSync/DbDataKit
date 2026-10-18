using System.Reflection;
using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;
using DbDataBuild.Sql.Ast;

namespace DbDataBuild.Sql.Matrix;

/// <summary>Loads matrix/constructs.yml and matrix/covered.yml (embedded in the assembly, or read from a directory) with strict YAML rules.</summary>
public static class MatrixLoader
{
    public const string ConstructsFile = "constructs.yml";
    public const string CoveredFile = "covered.yml";
    public const string StrategiesFile = "strategies.yml";

    private static readonly string[] RowKeys = ["id", "detect", "duckdb", "tsql", "sqlserver", "fabric", "postgres"];
    private static readonly string[] EntryKeys = ["status", "min_version", "note", "test"];

    public static SupportMatrix LoadEmbedded(List<Diagnostic> diags) =>
        Load(n => ReadEmbedded(n), diags);

    /// <summary>A short, stable identifier of the embedded matrix data (a hash of its files), written into rendered files so a matrix change shows up in them.</summary>
    public static string EmbeddedVersion()
    {
        var text = string.Join("\n---\n", new[] { ConstructsFile, CoveredFile, StrategiesFile }.Select(ReadEmbedded)).Replace("\r\n", "\n");
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
    }

    public static SupportMatrix LoadFromDirectory(string dir, List<Diagnostic> diags) =>
        Load(n => File.ReadAllText(Path.Combine(dir, n)), diags);

    private static string ReadEmbedded(string name)
    {
        using var s = typeof(MatrixLoader).Assembly.GetManifestResourceStream("matrix/" + name)
            ?? throw new InvalidOperationException($"matrix/{name} is not embedded.");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static SupportMatrix Load(Func<string, string> read, List<Diagnostic> diags)
    {
        var rows = new List<ConstructRow>();
        var covered = new List<CoverageEntry>();
        var seq = ReadSequence(read(ConstructsFile), "matrix/" + ConstructsFile, diags);
        if (seq != null)
            foreach (var item in seq.Items)
                if (ReadRow(item, "matrix/" + ConstructsFile, diags) is { } row) rows.Add(row);

        var ids = new HashSet<string>();
        foreach (var r in rows.Where(r => !ids.Add(r.Id)))
            diags.Add(new Diagnostic(DiagnosticCatalog.DuplicateKey, new(r.File, r.Line, 1), $"Matrix row id `{r.Id}` appears more than once."));

        var cseq = ReadSequence(read(CoveredFile), "matrix/" + CoveredFile, diags);
        if (cseq != null)
            foreach (var item in cseq.Items)
                if (ReadCovered(item, "matrix/" + CoveredFile, diags) is { } c) covered.Add(c);

        var strategies = new List<ConstructRow>();
        var sseq = ReadSequence(read(StrategiesFile), "matrix/" + StrategiesFile, diags);
        if (sseq != null)
            foreach (var item in sseq.Items)
                if (ReadRow(item, "matrix/" + StrategiesFile, diags, requireDetect: false) is { } row) strategies.Add(row);
        foreach (var r in strategies.GroupBy(r => r.Id).Where(g => g.Count() > 1).SelectMany(g => g.Skip(1)))
            diags.Add(new Diagnostic(DiagnosticCatalog.DuplicateKey, new(r.File, r.Line, 1), $"Strategy row id `{r.Id}` appears more than once."));
        return new SupportMatrix(rows, covered, strategies);
    }

    private static YamlSequence? ReadSequence(string text, string file, List<Diagnostic> diags)
    {
        var root = StrictYamlReader.Read(text, file, diags);
        if (root == null) return null;
        if (root is YamlSequence s) return s;
        diags.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(file, root.Line, root.Column), "The file must be a list of entries."));
        return null;
    }

    private static ConstructRow? ReadRow(YamlNode node, string file, List<Diagnostic> diags, bool requireDetect = true)
    {
        void Err(DiagnosticDescriptor d, YamlNode at, string found) => diags.Add(new Diagnostic(d, new(file, at.Line, at.Column), found));
        if (node is not YamlMapping m) { Err(DiagnosticCatalog.InvalidValue, node, "A matrix row must be a mapping."); return null; }
        var allowedKeys = requireDetect ? RowKeys : RowKeys.Where(k => k != "detect").ToArray();
        foreach (var e in m.Entries.Where(e => !allowedKeys.Contains(e.Key.Value)))
            Err(DiagnosticCatalog.UnknownKey, e.Key, $"Unknown key `{e.Key.Value}` in a matrix row. Keys: {string.Join(", ", allowedKeys)}.");

        var id = (m.Get("id") as YamlScalar)?.Value;
        if (string.IsNullOrEmpty(id)) { Err(DiagnosticCatalog.MissingKey, m, "A matrix row needs an `id`."); return null; }

        var detect = new List<DetectRule>();
        var dnode = m.Get("detect");
        var dscalars = dnode switch { YamlScalar s => [s], YamlSequence q => q.Items.OfType<YamlScalar>().ToList(), _ => [] };
        if (dscalars.Count == 0 && requireDetect) Err(DiagnosticCatalog.MissingKey, m, $"Row `{id}` needs `detect:` (a string or list).");
        foreach (var d in dscalars)
            if (ParseDetect(d.Value, out var rule, out var problem)) detect.Add(rule!);
            else Err(DiagnosticCatalog.InvalidValue, d, $"Row `{id}`: {problem}");

        if (m.Get("duckdb") is not YamlScalar { Value: "native" })
            Err(DiagnosticCatalog.InvalidValue, (YamlNode?)m.Get("duckdb") ?? m, $"Row `{id}`: `duckdb:` must be `native` (DuckDB is the canonical dialect).");

        var targets = new Dictionary<string, MatrixEntry>();
        if (m.Get("tsql") is { } tsql)
        {
            if (tsql is not YamlMapping tm) Err(DiagnosticCatalog.InvalidValue, tsql, $"Row `{id}`: `tsql:` must map `any`, `sqlserver` or `fabric` to entries.");
            else
                foreach (var e in tm.Entries)
                {
                    var keys = e.Key.Value switch { "any" => new[] { "sqlserver", "fabric" }, "sqlserver" or "fabric" => [e.Key.Value], _ => [] };
                    if (keys.Length == 0) { Err(DiagnosticCatalog.InvalidValue, e.Key, $"Row `{id}`: unknown tsql key `{e.Key.Value}`. Use any, sqlserver or fabric."); continue; }
                    if (ReadEntry(e.Value, id, e.Key.Value, file, diags) is { } entry) foreach (var k in keys) targets[k] = entry;
                }
        }
        foreach (var t in SupportMatrix.Targets)
            if (m.Get(t) is { } tn && ReadEntry(tn, id, t, file, diags) is { } entry) targets[t] = entry;
        foreach (var t in SupportMatrix.Targets.Where(t => !targets.ContainsKey(t)))
            Err(DiagnosticCatalog.MissingKey, m, $"Row `{id}` has no entry for target `{t}`. Every target needs an explicit status.");

        return new ConstructRow(id, detect, targets, file, m.Line);
    }

    private static MatrixEntry? ReadEntry(YamlNode node, string id, string target, string file, List<Diagnostic> diags)
    {
        void Err(DiagnosticDescriptor d, YamlNode at, string found) => diags.Add(new Diagnostic(d, new(file, at.Line, at.Column), found));
        if (node is not YamlMapping m) { Err(DiagnosticCatalog.InvalidValue, node, $"Row `{id}`/{target}: entry must be a mapping with a `status`."); return null; }
        foreach (var e in m.Entries.Where(e => !EntryKeys.Contains(e.Key.Value)))
            Err(DiagnosticCatalog.UnknownKey, e.Key, $"Row `{id}`/{target}: unknown key `{e.Key.Value}`. Keys: {string.Join(", ", EntryKeys)}.");

        if (m.Get("status") is not YamlScalar st) { Err(DiagnosticCatalog.MissingKey, m, $"Row `{id}`/{target}: `status` is required."); return null; }
        if (!Enum.TryParse<SupportStatus>(st.Value, ignoreCase: true, out var status) || st.Value != st.Value.ToLowerInvariant() ||
            !Enum.IsDefined(status))
        { Err(DiagnosticCatalog.InvalidValue, st, $"Row `{id}`/{target}: unknown status `{st.Value}`. Supported: native, translated, approximated, emulated, unsupported, unverified."); return null; }

        int? minVersion = null;
        if (m.Get("min_version") is YamlScalar mv)
        {
            if (int.TryParse(mv.Value, out var v) && v > 0) minVersion = v;
            else Err(DiagnosticCatalog.InvalidValue, mv, $"Row `{id}`/{target}: min_version must be a positive integer.");
        }
        var test = (m.Get("test") as YamlScalar)?.Value;
        if (status is not (SupportStatus.Native or SupportStatus.Unverified) && string.IsNullOrEmpty(test))
            Err(DiagnosticCatalog.MissingKey, m, $"Row `{id}`/{target}: status `{st.Value}` needs a `test:` (every non-native row is backed by a conformance test).");
        return new MatrixEntry(status, minVersion, (m.Get("note") as YamlScalar)?.Value, test);
    }

    private static bool ParseDetect(string text, out DetectRule? rule, out string problem)
    {
        rule = null; problem = "";
        var i = text.IndexOf(':');
        if (i <= 0 || i == text.Length - 1) { problem = $"detect `{text}` must look like node:<tag>, fn:<NAME> or detector:<name>."; return false; }
        var (kind, name) = (text[..i], text[(i + 1)..]);
        switch (kind)
        {
            case "node":
                if (!AstNode.KnownTypes.Contains(name)) { problem = $"`{name}` is not a polyglot Expression tag."; return false; }
                rule = new DetectRule(DetectKind.Node, name); return true;
            case "fn":
                rule = new DetectRule(DetectKind.Function, name.ToUpperInvariant()); return true;
            case "detector":
                if (!Detectors.All.ContainsKey(name)) { problem = $"detector `{name}` does not exist. Known: {string.Join(", ", Detectors.All.Keys)}."; return false; }
                rule = new DetectRule(DetectKind.Detector, name); return true;
            default:
                problem = $"unknown detect kind `{kind}`."; return false;
        }
    }

    private static CoverageEntry? ReadCovered(YamlNode node, string file, List<Diagnostic> diags)
    {
        void Err(DiagnosticDescriptor d, YamlNode at, string found) => diags.Add(new Diagnostic(d, new(file, at.Line, at.Column), found));
        if (node is not YamlMapping m) { Err(DiagnosticCatalog.InvalidValue, node, "A covered entry must be a mapping."); return null; }
        foreach (var e in m.Entries.Where(e => e.Key.Value is not ("node" or "function" or "datatype" or "evidence")))
            Err(DiagnosticCatalog.UnknownKey, e.Key, $"Unknown key `{e.Key.Value}`. Keys: node, function, datatype, evidence.");
        var kind = m.Get("node") is YamlScalar ? DetectKind.Node : m.Get("datatype") is YamlScalar ? DetectKind.DataType : DetectKind.Function;
        var name = ((m.Get("node") ?? m.Get("datatype") ?? m.Get("function")) as YamlScalar)?.Value;
        if (string.IsNullOrEmpty(name)) { Err(DiagnosticCatalog.MissingKey, m, "A covered entry needs `node:`, `function:` or `datatype:`."); return null; }
        if (kind == DetectKind.Node && !AstNode.KnownTypes.Contains(name)) { Err(DiagnosticCatalog.InvalidValue, m, $"`{name}` is not a polyglot Expression tag."); return null; }
        var evidence = (m.Get("evidence") as YamlSequence)?.Items.OfType<YamlScalar>().Select(s => s.Value).ToList() ?? [];
        if (evidence.Count == 0) Err(DiagnosticCatalog.MissingKey, m, $"`{name}` needs `evidence:` (the cases that show it works).");
        return new CoverageEntry(kind, name, evidence, file, m.Line);
    }
}
