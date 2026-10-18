using DbDataBuild.Core;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>A cell of a test row. <c>Text</c> is the value as written (always a string, cast by DuckDB to the column's declared type); null is SQL NULL.</summary>
public sealed record TestValue(string? Text, int Line);

/// <summary>One row of `given` or `expect`: column name to value. A column left out is NULL.</summary>
public sealed record TestRowData(IReadOnlyDictionary<string, TestValue> Values, int Line);

/// <summary>The rows to put in one table the model's query reads.</summary>
public sealed record GivenTable(string Table, IReadOnlyList<TestRowData> Rows, int Line);

/// <param name="Expect">The rows the query must return (null: not checked).</param>
/// <param name="Ordered">The rows must come back in this order.</param>
/// <param name="Assert">A DuckDB SELECT over a table named `result` that returns violations (null: none).</param>
public sealed record ModelTestCase(string Name, int Line, IReadOnlyList<GivenTable> Given, IReadOnlyList<TestRowData>? Expect, bool Ordered, string? Assert);

/// <summary>A model test file (`tests/models/&lt;name&gt;.yml`, DESIGN.md 9.8): cases of given rows and what the model's query must return for them.</summary>
public sealed record ModelTestFile(string Name, string File, string Model, string? Description, string Severity, IReadOnlyList<string> Tags, IReadOnlyList<ModelTestCase> Cases);

public static class ModelTestLoader
{
    public const string Directory = "tests/models";

    public static IReadOnlyList<string> Discover(string projectRoot)
    {
        var dir = Path.Combine(projectRoot, Directory);
        if (!System.IO.Directory.Exists(dir)) return [];
        return System.IO.Directory.EnumerateFiles(dir, "*.yml", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(projectRoot, f).Replace('\\', '/')).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    /// <summary>`tests/models/marts/fct_orders.yml` is the test `marts.fct_orders`, which is also the model it tests unless `model:` says otherwise.</summary>
    public static string NameOf(string file) => file[(Directory.Length + 1)..^".yml".Length].Replace('/', '.');

    public static ModelTestFile? Load(string text, string file, List<Diagnostic> diags)
    {
        var before = diags.Count;
        var (description, severity, tags) = TestHeader.Parse(text.Replace("\r\n", "\n").Split('\n'), "#", file, "a model test", diags);
        var root = StrictYamlReader.Read(text, file, diags);
        if (root == null)
        {
            if (diags.Count == before) diags.Add(new Diagnostic(DiagnosticCatalog.MissingKey, new(file, 1, 1), "The file is empty. Required key: cases."));
            return null;
        }
        var name = NameOf(file);
        var result = new Reader(file, diags).Read(root, name);
        return diags.Count > before || result == null ? null : new ModelTestFile(name, file, result.Value.Model, description, severity ?? "error", tags ?? [], result.Value.Cases);
    }

    private sealed class Reader(string file, List<Diagnostic> diags) : YamlFieldReader(file, diags)
    {
        public (string Model, List<ModelTestCase> Cases)? Read(YamlNode root, string defaultModel)
        {
            if (root is not YamlMapping top) { Add(DiagnosticCatalog.InvalidValue, root, "A model test must be a mapping with `cases`."); return null; }
            CheckKeys(top, ["model", "cases"], "a model test");
            var model = Scalar(top, "model", required: false, at: top)?.Value ?? defaultModel;
            var cases = new List<ModelTestCase>();
            if (top.Get("cases") is not { } casesNode) { Add(DiagnosticCatalog.MissingKey, top, "Required key `cases` is missing.", fix: "Add `cases:` with at least one case."); return (model, cases); }
            if (casesNode is not YamlSequence seq || seq.Items.Count == 0) { Add(DiagnosticCatalog.InvalidValue, casesNode, "`cases` must be a non-empty list."); return (model, cases); }
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in seq.Items)
                if (ReadCase(item) is { } c)
                {
                    if (!names.Add(c.Name)) Add(DiagnosticCatalog.DuplicateKey, item, $"The case `{c.Name}` is defined more than once.");
                    cases.Add(c);
                }
            return (model, cases);
        }

        private ModelTestCase? ReadCase(YamlNode node)
        {
            if (node is not YamlMapping map) { Add(DiagnosticCatalog.InvalidValue, node, "Each case must be a mapping with `name` and `expect` or `assert`."); return null; }
            CheckKeys(map, ["name", "given", "expect", "assert"], "a case");
            var name = Scalar(map, "name", required: true, at: map);
            var given = new List<GivenTable>();
            if (map.Get("given") is { } g)
            {
                if (g is not YamlMapping gm) Add(DiagnosticCatalog.InvalidValue, g, "`given` must map each table name to a list of rows.");
                else
                    foreach (var e in gm.Entries)
                    {
                        if (given.Any(x => x.Table == e.Key.Value)) { Add(DiagnosticCatalog.DuplicateKey, e.Key, $"The table `{e.Key.Value}` is given twice."); continue; }
                        if (ReadRows(e.Value, $"given.{e.Key.Value}") is { } rows) given.Add(new GivenTable(e.Key.Value, rows, e.Key.Line));
                    }
            }

            List<TestRowData>? expect = null;
            var ordered = false;
            if (map.Get("expect") is { } x)
            {
                if (x is YamlSequence) expect = ReadRows(x, "expect");
                else if (x is YamlMapping xm)
                {
                    CheckKeys(xm, ["rows", "ordered"], "`expect`");
                    if (xm.Get("rows") is { } rows) expect = ReadRows(rows, "expect.rows");
                    else Add(DiagnosticCatalog.MissingKey, xm, "Required key `rows` is missing in `expect`.", fix: "Add `rows:` (a list; `[]` for no rows).");
                    if (Scalar(xm, "ordered", required: false, at: xm) is { } o)
                    {
                        if (o.Value is "true" or "false") ordered = o.Value == "true";
                        else Add(DiagnosticCatalog.InvalidValue, o, $"ordered is `{o.Value}`.", "true or false (lowercase).");
                    }
                }
                else Add(DiagnosticCatalog.InvalidValue, x, "`expect` must be a list of rows, or a mapping with `rows` and optionally `ordered`.");
            }
            string? assertSql = null;
            if (map.Get("assert") is { } a)
            {
                if (a is YamlScalar s && s.Value.Trim().Length > 0) assertSql = s.Value;
                else Add(DiagnosticCatalog.InvalidValue, a, "`assert` must be a SELECT over the table `result` that returns the violating rows.");
            }
            if (expect == null && assertSql == null && map.Get("expect") == null && map.Get("assert") == null)
                Add(DiagnosticCatalog.MissingKey, map, "A case needs `expect` (rows) or `assert` (a query), or both.", fix: "Add `expect:` or `assert:`.");
            return name == null ? null : new ModelTestCase(name.Value, name.Line, given, expect, ordered, assertSql);
        }

        private List<TestRowData>? ReadRows(YamlNode node, string where)
        {
            if (node is not YamlSequence seq) { Add(DiagnosticCatalog.InvalidValue, node, $"`{where}` must be a list of rows (a row is a mapping of column to value)."); return null; }
            var rows = new List<TestRowData>();
            foreach (var item in seq.Items)
            {
                if (item is not YamlMapping rm) { Add(DiagnosticCatalog.InvalidValue, item, $"Each row of `{where}` must be a mapping of column to value."); continue; }
                var values = new Dictionary<string, TestValue>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in rm.Entries)
                {
                    if (values.ContainsKey(e.Key.Value)) { Add(DiagnosticCatalog.DuplicateKey, e.Key, $"Column `{e.Key.Value}` is given twice in a row."); continue; }
                    if (e.Value is not YamlScalar v) { Add(DiagnosticCatalog.InvalidValue, e.Value, $"The value of `{e.Key.Value}` must be a scalar."); continue; }
                    // all scalars are strings: a plain null, ~ or empty value is NULL; the text "null" is written in quotes
                    values[e.Key.Value] = new TestValue(!v.Quoted && v.Value is "" or "null" or "~" or "Null" or "NULL" ? null : v.Value, v.Line);
                }
                rows.Add(new TestRowData(values, rm.Line));
            }
            return rows;
        }
    }
}
