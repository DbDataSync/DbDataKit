using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Define;
using DbDataBuild.Models;
using DbDataKit.Quality;
using Json.Schema;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Plan 23a FQ02: checks are stored with the dataset they are about, in the model's file (format 2, `schemaVersion: 2`): in its own `checks:` and in a column's declaration. They are read through the shared check model
/// (<c>DbDataKit.Quality</c>), validated against the model's columns, shown by `project show metadata`, and never evaluated here. A format-1 file that has the section, and a column the model does not have, are refused with file and line.
/// </summary>
public class ModelChecksTests
{
    private const string File = "models/marts/fct_orders.yml";
    private const string Header = "schemaVersion: 2\nname: marts.fct_orders\nkind: full\n";
    private const string Cols = "columns:\n  - {name: order_id, type: BIGINT, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n";

    private static ModelDefinition Loaded(string yaml)
    {
        var (def, diags) = Load(yaml);
        Assert.True(def is not null, string.Join("\n", diags.Select(DiagnosticFormatter.Format)));
        return def!;
    }

    private static Diagnostic Refused(string yaml, string code)
    {
        var (def, diags) = Load(yaml);
        Assert.Null(def);
        return diags.First(d => d.Code == code);
    }

    [Fact]
    public void A_model_carries_checks_in_its_own_section_and_in_its_columns_with_the_dataset_and_column_filled_in()
    {
        var def = Loaded(Header +
            "columns:\n  - {name: order_id, type: BIGINT, nullable: false, checks: [{id: id_unique, kind: unique, severity: block}]}\n  - {name: amount, type: \"DECIMAL(14, 2)\", checks: [{id: amount_range, kind: range, min: 0, max: 100000}, {id: amount_fresh, kind: freshness, max_age: 6h}]}\n" +
            "checks:\n  - {id: rows_stable, kind: row_count_change, max_decrease_percent: 20}\n  - {id: id_present, kind: not_null, column: order_id, on_error: block}\n");

        Assert.Equal(2, def.SchemaVersion);
        Assert.Equal(["id_unique", "amount_range", "amount_fresh", "rows_stable", "id_present"], def.Checks.Select(c => c.Check.Id));
        Assert.All(def.Checks, c => Assert.Equal("marts.fct_orders", c.Check.Attachment.Dataset));

        var unique = def.Checks[0];
        Assert.Equal((CheckKind.Unique, "order_id", "order_id", CheckSeverity.Block), (unique.Check.Kind, unique.InColumn, unique.Check.Attachment.Column, unique.Check.Severity));
        var range = Assert.IsType<RangeParameters>(def.Checks[1].Check.Parameters);
        Assert.Equal(("0", "100000"), (range.Min!.Text, range.Max!.Text));
        Assert.Equal("amount", def.Checks[2].Check.Attachment.Column);        // a freshness check in a column's declaration is about that column
        Assert.Equal(TimeSpan.FromHours(6), Assert.IsType<FreshnessParameters>(def.Checks[2].Check.Parameters).MaxAge);
        Assert.Null(def.Checks[3].InColumn);
        Assert.Empty(def.Checks[3].Check.Attachment.Columns);
        Assert.Equal(CheckOnError.Block, def.Checks[4].Check.OnError);
        Assert.True(def.Checks.All(c => c.Line > 0));
    }

    [Fact]
    public void A_file_without_checks_is_format_1_and_loads_as_before()
    {
        var def = Loaded("name: marts.fct_orders\nkind: full\n" + Cols);
        Assert.Equal(1, def.SchemaVersion);
        Assert.Empty(def.Checks);
        Assert.Equal(1, Loaded("schemaVersion: 1\nname: marts.fct_orders\nkind: full\n" + Cols).SchemaVersion);
        Assert.Equal(2, ModelDefinitionLoader.Version);
    }

    [Fact]
    public void A_copy_has_checks_on_the_columns_of_its_origin_which_are_not_compared()
    {
        var def = Loaded(Header.Replace("kind: full", "kind: {copy: {from: pos.orders}}") + "checks:\n  - {id: n, kind: not_null, column: anything_the_origin_has}\n");
        Assert.Equal("anything_the_origin_has", Assert.Single(def.Checks).Check.Attachment.Column);
    }

    [Fact]
    public void A_check_on_a_column_the_model_does_not_have_is_refused_with_file_and_line()
    {
        var d = Refused(Header + Cols + "checks:\n  - {id: ok, kind: not_null, column: order_id}\n  - id: ghost_present\n    kind: not_null\n    column: ghost\n", "DDB-217");
        Assert.Equal((File, 11), (d.Location.File, d.Location.Line));
        Assert.Equal("Check `ghost_present` is about `ghost`, which is not declared in `columns`.", d.Found);

        var several = Refused(Header + Cols + "checks:\n  - {id: u, kind: unique, columns: [order_id, ghost]}\n", "DDB-217");
        Assert.Equal(8, several.Location.Line);
        Assert.Contains("`ghost`", several.Found);
    }

    [Fact]
    public void A_file_of_the_first_format_that_has_checks_is_refused_naming_the_file_and_the_line()
    {
        var d = Refused("name: marts.fct_orders\nkind: full\n" + Cols + "checks:\n  - {id: x, kind: not_null, column: order_id}\n", "DDB-623");
        Assert.Equal((File, 6), (d.Location.File, d.Location.Line));
        Assert.Equal($"`checks` is a section of format 2 of the model file, and `{File}` does not say `schemaVersion: 2`.", d.Found);
        Assert.Contains("Add `schemaVersion: 2`", d.Fix);

        // a check in a column's declaration is the same section
        var inColumn = Refused("name: marts.fct_orders\nkind: full\ncolumns:\n  - {name: a, type: INT, checks: [{id: x, kind: not_null}]}\n", "DDB-623");
        Assert.Equal(4, inColumn.Location.Line);
        Assert.Contains(File, inColumn.Found);
    }

    [Fact]
    public void A_newer_format_than_the_tool_reads_is_refused_with_both_numbers_and_the_file()
    {
        var d = Refused("schemaVersion: 3\nname: marts.fct_orders\nkind: full\n" + Cols, "DDB-623");
        Assert.Equal((File, 1), (d.Location.File, d.Location.Line));
        Assert.Equal($"`schemaVersion` is 3, but this tool reads model files up to 2 (format 1 is read as well); `{File}` was written by a newer version.", d.Found);
        Assert.Equal("Upgrade the tool.", d.Fix);

        Assert.Contains("positive whole number", Refused("schemaVersion: 0\nname: marts.fct_orders\nkind: full\n" + Cols, "DDB-623").Found);
    }

    /// <summary>
    /// The clean break for a tool older than this one: its model loader allowed exactly the keys of the first format, so a file with `schemaVersion` or `checks` fails there as DDB-104 (an unknown key) naming the file and the
    /// line, and no old tool builds the model without its checks. That behaviour was run on the loader before this slice (`project compile` printed `error DDB-104 models/marts/orders.yml:1 Unknown key schemaVersion`); the list here
    /// is the old loader's, and this test fails if the first format's keys change, which is what would make the statement untrue.
    /// </summary>
    [Fact]
    public void The_first_formats_keys_are_unchanged_so_an_older_tool_refuses_a_format_2_file_as_an_unknown_key()
    {
        string[] formatOneKeys = ["name", "kind", "grain", "connections", "columns", "renames", "loads", "indexes", "hooks", "lint_ignore", "rewrites", "parameters", "refresh", "keys", "foreign_keys", "title", "description", "tags", "properties"];
        var d = Refused(Header + Cols + "bogus: 1\n", "DDB-104");
        foreach (var key in formatOneKeys) Assert.Contains(key, d.Supported!);
        Assert.Equal(formatOneKeys.Length + 2, d.Supported!.Split(',').Length);          // the first format's keys, `schemaVersion` and `checks`, and nothing else
        Assert.Contains("schemaVersion", d.Supported);
        Assert.Contains("checks", d.Supported);
        Assert.Equal((File, 7), (d.Location.File, d.Location.Line));
    }

    [Theory]
    [InlineData("{id: x, kind: not_nul, column: order_id}", 6, "DBD-QLT-001")]
    [InlineData("{id: x, kind: range, column: amount, min: 9, max: 1}", 6, "DBD-QLT-002")]
    [InlineData("{id: x, kind: not_null}", 6, "needs the column")]
    [InlineData("{id: x, kind: not_null, column: order_id, severity: fatal}", 6, "DBD-QLT-004")]
    [InlineData("{id: x, kind: referential, column: order_id}", 6, "DBD-QLT-003")]
    public void A_check_that_is_not_valid_is_refused_with_file_line_and_the_shared_code(string entry, int line, string expected)
    {
        var d = Refused(Header + Cols + "checks:\n  - " + entry + "\n", "DDB-622");
        Assert.Equal(File, d.Location.File);
        Assert.True(d.Location.Line >= line + 2, d.ToString());
        Assert.Contains(expected, d.Found);
    }

    [Fact]
    public void A_check_about_another_model_and_a_repeated_id_are_refused()
    {
        var other = Refused(Header + Cols + "checks:\n  - {id: x, kind: not_null, column: order_id, dataset: marts.other}\n", "DDB-622");
        Assert.Equal(8, other.Location.Line);
        Assert.Equal("A check in a model's file is about that model (`marts.fct_orders`); this one is about `marts.other`.", other.Found);

        var twice = Refused(Header + "columns:\n  - {name: a, type: INT, checks: [{id: x, kind: not_null}]}\nchecks:\n  - {id: x, kind: unique, column: a}\n", "DDB-622");
        Assert.Equal(7, twice.Location.Line);
        Assert.Equal("The check id `x` is used twice in this model.", twice.Found);
    }

    [Fact]
    public void A_check_in_a_column_is_about_that_column_and_never_names_its_own()
    {
        var named = Refused(Header + "columns:\n  - {name: a, type: INT, checks: [{id: x, kind: not_null, column: b}]}\n", "DDB-622");
        Assert.Equal("A check in the declaration of column `a` is about that column: `column` is not written there.", named.Found);
        Assert.Equal(5, named.Location.Line);

        var rows = Refused(Header + "columns:\n  - {name: a, type: INT, checks: [{id: x, kind: row_count_change, max_decrease_percent: 5}]}\n", "DDB-622");
        Assert.Equal("`row_count_change` is about the whole model, not about column `a`.", rows.Found);
    }

    [Fact]
    public void A_mapped_model_is_not_owned_by_the_tool_and_takes_no_checks()
    {
        var diags = new List<Diagnostic>();
        var mapped = "name: staging.orders\nkind: mapped\ncolumns:\n  - {name: a, type: INT}\nchecks:\n  - {id: x, kind: not_null, column: a}\n";
        Assert.Null(DeclaredModelLoader.Load(mapped, "models/staging/orders.yml", "staging.orders", diags));
        Assert.Contains(diags, d => d.Code == "DDB-104" && d.Location.Line == 5);
    }

    [Fact]
    public void A_template_project_with_checks_loads_and_a_check_on_a_missing_column_in_it_is_refused_with_file_and_line()
    {
        var template = Path.Combine(PolyglotBindingTests.RepoRoot(), "templates", "starter");
        var project = ProjectValidator.Validate(template);
        Assert.Empty(project.Diagnostics.Where(d => d.Severity == Severity.Error).Select(DiagnosticFormatter.Format));
        var orders = project.Models.Single(m => m.Name == "marts.orders");
        Assert.Equal(2, orders.SchemaVersion);
        Assert.Equal(["orders_order_id_unique", "orders_status_known", "orders_customer_present", "orders_amount_not_negative", "orders_fresh", "orders_row_count_stable"], orders.Checks.Select(c => c.Check.Id));
        Assert.Equal(["order_id", "status"], orders.Checks.Where(c => c.InColumn is not null).Select(c => c.InColumn));
        Assert.Equal(CheckKind.Freshness, orders.Checks.Single(c => c.Check.Id == "orders_fresh").Check.Kind);

        var copy = Path.Combine(Path.GetTempPath(), "ddb-checks-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyTree(template, copy);
            var file = Path.Combine(copy, "models", "marts", "orders.yml");
            var text = System.IO.File.ReadAllText(file).Replace("column: customer_id, severity: block}", "column: customer_ident, severity: block}");
            TestFiles.WriteAllText(file, text);
            var refused = ProjectValidator.Validate(copy).Diagnostics.Single(d => d.Code == "DDB-217");
            Assert.Equal("models/marts/orders.yml", refused.Location.File.Replace('\\', '/'));
            Assert.Equal(text.Split('\n').ToList().FindIndex(l => l.Contains("customer_ident")) + 1, refused.Location.Line);
            Assert.Contains("`customer_ident`", refused.Found);
        }
        finally { DeleteTree(copy); }
    }

    [Fact]
    public void Project_show_metadata_shows_the_checks_and_the_document_satisfies_the_metadata_schema()
    {
        var template = Path.Combine(PolyglotBindingTests.RepoRoot(), "templates", "starter");
        var o = new StringWriter();
        var exit = CliApp.Run(["project", "show", "metadata", "marts.orders", "marts.customers", "--project", template, "--format", "json"], o, new StringWriter(), environment: _ => null);
        Assert.Equal(0, exit);
        var models = JsonNode.Parse(o.ToString())!["data"]!["models"]!.AsArray();
        var orders = models.Single(m => (string?)m!["name"] == "marts.orders")!;
        var customers = models.Single(m => (string?)m!["name"] == "marts.customers")!;

        Assert.Null(customers["checks"]);                                     // a model with none leaves the key out
        var checks = orders["checks"]!.AsArray();
        Assert.Equal(6, checks.Count);
        var unique = checks[0]!;
        Assert.Equal(("orders_order_id_unique", "unique", "block", "alert", "order_id"), ((string)unique["id"]!, (string)unique["kind"]!, (string)unique["severity"]!, (string)unique["on_error"]!, (string)unique["in_column"]!));
        Assert.Equal(["order_id"], unique["columns"]!.AsArray().Select(c => (string)c!));
        Assert.Equal(["placed", "shipped", "completed", "return_pending", "returned"], checks[1]!["parameters"]!["values"]!.AsArray().Select(v => (string)v!));
        Assert.Null(checks[2]!["in_column"]);
        Assert.Equal("0", (string?)checks[3]!["parameters"]!["min"]);
        Assert.Equal("2d", (string?)checks[4]!["parameters"]!["max_age"]);
        Assert.Equal(50m, (decimal)checks[5]!["parameters"]!["max_decrease_percent"]!);

        var metadata = SchemaConformanceTests.LoadSchema("metadata");
        bool Valid(JsonNode n) => metadata.Evaluate(JsonSerializer.SerializeToNode(n), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
        Assert.True(Valid(orders));
        var broken = JsonNode.Parse(orders.ToJsonString())!;
        broken["checks"]![0]!["kind"] = "sql";
        Assert.False(Valid(broken));
    }

    [Fact]
    public void Renaming_a_column_follows_into_the_checks_that_name_it_and_a_removed_column_is_refused_on_the_next_load()
    {
        var text = Header.Replace("\r", "") + "columns:\n  - {name: order_id, type: BIGINT}\n  - name: amount\n    type: INT\n    checks: [{id: a, kind: not_null}]\nchecks:\n  - {id: n, kind: not_null, column: amount}\n  - {id: u, kind: unique, columns: [order_id, amount]}\n  - {id: r, kind: referential, column: order_id, references: {dataset: marts.other, column: amount}}\n";
        var (renamed, problem) = DefinitionEditor.Apply(text, File, [new RenameColumn("amount", "total")]);
        Assert.Null(problem);
        Assert.Contains("{id: n, kind: not_null, column: total}", renamed);
        Assert.Contains("columns: [order_id, total]", renamed);
        Assert.Contains("references: {dataset: marts.other, column: amount}", renamed);          // the column of another dataset is not this model's
        var def = Loaded(renamed!);
        Assert.Equal(["total"], def.Checks.Single(c => c.Check.Id == "n").Check.Attachment.Columns);
        Assert.Equal("total", def.Checks.Single(c => c.Check.Id == "a").Check.Attachment.Column);

        var (removed, _) = DefinitionEditor.Apply(text, File, [new RemoveColumn("amount")]);
        var (after, diags) = Load(removed!);
        Assert.Null(after);
        Assert.Contains(diags, d => d.Code == "DDB-217" && d.Found.Contains("`amount`"));        // the model's own checks still name it: refused with the line, never dropped silently
    }

    /// <summary>
    /// The model schema's definitions of a check are the check model's (<c>check.schema.json</c> of <c>DbDataKit.Quality</c>): the model-level entry is its entry and the column-level entry is the same without what the declaration says
    /// (<c>dataset</c>, <c>column</c>, <c>columns</c>, and the kind about the whole dataset). Published schemas stand alone, so they are copies; this keeps them equal.
    /// </summary>
    [Fact]
    public void The_model_schema_s_check_definitions_are_the_check_models()
    {
        var shared = JsonNode.Parse(CheckSchema.Text())!["$defs"]!["check"]!;
        var model = JsonNode.Parse(System.IO.File.ReadAllText(Path.Combine(PolyglotBindingTests.RepoRoot(), "schemas", "model.schema.json")))!["$defs"]!;

        JsonNode WithoutDescription(JsonNode n) { var c = JsonNode.Parse(n.ToJsonString())!; c.AsObject().Remove("description"); return c; }
        Assert.Equal(WithoutDescription(shared).ToJsonString(), WithoutDescription(model["check"]!).ToJsonString());

        var column = JsonNode.Parse(shared.ToJsonString())!;
        var branches = column["oneOf"]!.AsArray();
        foreach (var gone in branches.Where(b => (string?)b!["properties"]!["kind"]!["const"] == "row_count_change").ToList()) branches.Remove(gone);
        foreach (var branch in branches.Select(b => b!.AsObject()))
        {
            foreach (var key in new[] { "dataset", "column", "columns" }) branch["properties"]!.AsObject().Remove(key);
            var required = branch["required"]!.AsArray();
            foreach (var r in required.Where(r => (string?)r == "column").ToList()) required.Remove(r);
            if (branch["oneOf"] is JsonArray choice && choice.All(c => c!["required"]!.AsArray().All(r => (string?)r is "column" or "columns"))) branch.Remove("oneOf");
        }
        Assert.Equal(WithoutDescription(column).ToJsonString(), WithoutDescription(model["columnCheck"]!).ToJsonString());
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) System.IO.File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }

    private static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) System.IO.File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, true);
    }
}
