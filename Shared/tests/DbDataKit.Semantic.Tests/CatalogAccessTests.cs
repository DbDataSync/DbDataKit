using System.Text.Json.Nodes;
using DbDataKit.Access;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// Plan 22a GP01: format 2 of the semantic package gains the <c>access</c> section (QG2: rules are data). A catalog with it is format 2; each bad part is refused naming the file and the line, and checked against the catalog's tables,
/// columns and sources; the catalog schema holds the section's schema as the access library has it.
/// </summary>
public sealed class CatalogAccessTests
{
    private static string Sample => File.ReadAllText(Path.Combine(ConformanceTests.Root, "catalog", "catalog.yaml"));

    private const string RulesTable = """
          - name: region_access
            x_source: sqlserver_sales
            source: region_access
            columns:
              - {name: who, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: who}]}}
              - {name: region_code, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: region_code}]}}
              - {name: customer_limit, datatype: Integer, expression: {dialects: [{dialect: ANSI_SQL, expression: customer_limit}]}}
        """;

    private static string With(string access, string? extraTable = RulesTable)
    {
        var text = Sample.Replace("\ntables:\n", "\ntables:\n" + (extraTable is null ? "" : extraTable.Replace("\r\n", "\n").TrimStart('\n').TrimEnd() + "\n"), StringComparison.Ordinal);
        return text.TrimEnd() + "\n" + access.TrimEnd() + "\n";
    }

    private const string Good = """
        access:
          max_age: 2h
          row:
            - dataset: dim_customer
              groups:
                - id: by_region
                  rules:
                    - { id: r1, effect: include, table: region_access, subject: who, columns: { region: region_code } }
          columns:
            - { subject: interns, dataset: dim_customer, column: customer, treatment: mask, mask: hash }
        """;

    [Fact]
    public void A_catalog_with_an_access_section_loads_and_is_format_2()
    {
        Assert.Equal(1, CatalogLoader.LoadText(Sample, "sales.catalog.yaml").Format);
        Assert.Null(CatalogLoader.LoadText(Sample, "sales.catalog.yaml").Access);
        var catalog = CatalogLoader.LoadText(With(Good), "sales.catalog.yaml");
        Assert.Equal(2, catalog.Format);
        var access = catalog.Access!;
        Assert.Equal(TimeSpan.FromHours(2), access.MaxAge);
        Assert.Equal("region_access", access.Row.Single().Groups.Single().Rules.Single().Table);
        Assert.Equal(MaskKind.Hash, access.Columns.Single().Mask);
    }

    [Fact]
    public void An_imported_catalog_without_yaml_text_is_checked_the_same_way()
    {
        var json = CatalogLoader.YamlToJson(With(Good));
        Assert.Equal(2, CatalogLoader.LoadJson(json).Format);
        json["access"]!["row"]![0]!["groups"]![0]!["rules"]![0]!["table"] = "nowhere";
        var error = Assert.Throws<CatalogException>(() => CatalogLoader.LoadJson(json));
        Assert.Contains(error.Errors, e => e.Code == "invalid_access" && e.Message.Contains("nowhere", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""
        access:
          row:
            - dataset: dim_custmer
              groups:
                - { id: g, rules: [ { id: r, effect: include, table: region_access, subject: who, columns: { region: region_code } } ] }
        """, "DBD-ACC-001", "Did you mean `dim_customer`")]
    [InlineData("""
        access:
          row:
            - dataset: dim_customer
              groups:
                - { id: g, rules: [ { id: r, effect: include, table: region_access, subject: who, columns: { regon: region_code } } ] }
        """, "DBD-ACC-002", "Did you mean `region`")]
    [InlineData("""
        access:
          row:
            - dataset: dim_customer
              groups:
                - { id: g, rules: [ { id: r, effect: include, table: region_access, subject: whom, columns: { region: region_code } } ] }
        """, "DBD-ACC-002", "rules table `region_access`")]
    [InlineData("""
        access:
          row:
            - dataset: dim_customer
              groups:
                - { id: g, rules: [ { id: r, effect: include, table: not_a_table, subject: who, columns: { region: region_code } } ] }
        """, "DBD-ACC-003", "not a rules table")]
    [InlineData("""
        access:
          row:
            - dataset: dim_customer
              groups:
                - { id: g, rules: [ { id: r, effect: include, table: dim_product, subject: customer, columns: { region: category } } ] }
        """, "DBD-ACC-003", "a dimension, measure or relationship")]
    [InlineData("""
        access:
          row:
            - dataset: dim_customer
              groups:
                - { id: g, rules: [ { id: r, effect: include, table: fact_budget, subject: region, columns: { region: region } } ] }
        """, "DBD-ACC-003", "one target")]
    [InlineData("""
        access:
          row:
            - dataset: fact_budget
              groups:
                - { id: g, rules: [ { id: r, effect: include, table: region_access, subject: who, columns: { region: region_code } } ] }
        """, "DBD-ACC-010", "Analysis Services")]
    [InlineData("""
        access:
          row:
            - dataset: dim_customer
              groups:
                - { id: g, rules: [ { id: r, effect: include, table: region_access, subject: who, columns: { customer_key: customer_limit, region: region_code } } ] }
            - dataset: dim_customer
              groups:
                - { id: h, rules: [ { id: s, effect: include, table: region_access, subject: who, columns: { region: region_code } } ] }
        """, "DBD-ACC-007", "more than one entry")]
    [InlineData("""
        access:
          row:
            - dataset: dim_customer
              groups:
                - { id: g, rules: [ { id: r, effect: exclude, table: region_access, subject: who, columns: { region: region_code } } ] }
        """, "DBD-ACC-004", "no include rule")]
    [InlineData("""
        access:
          row:
            - dataset: dim_customer
              groups:
                - { id: g, rules: [ { id: r, effect: include, table: region_access, subject: who, columns: { customer_key: region_code } } ] }
        """, "DBD-ACC-006", "number")]
    [InlineData("""
        access:
          columns:
            - { subject: s, dataset: dim_customer, column: customer_key, treatment: mask }
        """, "DBD-ACC-008", "only text")]
    public void Each_bad_part_is_refused_naming_the_file_and_the_line(string access, string code, string message)
    {
        var text = With(access);
        var error = Assert.Throws<CatalogException>(() => CatalogLoader.LoadText(text, "sales.catalog.yaml"));
        var found = error.Errors.Where(e => e.Code == "invalid_access" && e.Message.Contains(code, StringComparison.Ordinal)).ToList();
        Assert.True(found.Count > 0, $"no {code}: " + string.Join(" | ", error.Errors.Select(e => e.Code + " " + e.Message)));
        Assert.Contains(found, e => e.Message.StartsWith("sales.catalog.yaml:", StringComparison.Ordinal) && e.Message.Contains(message, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(error.Errors, e => e.Path.StartsWith("$/access", StringComparison.Ordinal));
    }

    [Fact]
    public void A_shared_cache_beside_a_rule_is_refused()
    {
        var text = With(Good + "\ncache: {enabled: true, scope: shared}");
        var error = Assert.Throws<CatalogException>(() => CatalogLoader.LoadText(text, "sales.catalog.yaml"));
        Assert.Contains(error.Errors, e => e.Message.Contains("DBD-ACC-009", StringComparison.Ordinal));
        Assert.NotNull(CatalogLoader.LoadText(With(Good + "\ncache: {enabled: true, scope: rules}"), "sales.catalog.yaml").Access);
    }

    [Fact]
    public void A_key_the_schema_does_not_know_is_refused_in_the_schema_s_words_when_the_reader_has_none()
    {
        var error = Assert.Throws<CatalogException>(() => CatalogLoader.LoadText(With("access: {max_age: 1h, extra: 1}"), "sales.catalog.yaml"));
        Assert.Contains(error.Errors, e => e.Message.Contains("extra", StringComparison.Ordinal));
    }

    [Fact]
    public void The_catalog_schema_holds_the_access_schema_as_the_library_has_it()
    {
        var assembly = typeof(CatalogLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith(".catalog.schema.json", StringComparison.Ordinal)))!;
        var catalog = JsonNode.Parse(stream)!;
        var library = JsonNode.Parse(AccessSchema.Text())!;
        Assert.Equal("#/$defs/access", catalog["properties"]!["access"]!["$ref"]!.GetValue<string>());
        foreach (var (name, def) in library["$defs"]!.AsObject())
            Assert.True(JsonNode.DeepEquals(def, catalog["$defs"]![name]), $"$defs.{name} differs from the access library's schema");
    }

    [Fact]
    public void A_column_rule_of_a_plain_null_loads_through_the_json_stage()
    {
        var catalog = CatalogLoader.LoadText(With("access:\n  columns:\n    - { subject: s, dataset: dim_customer, column: customer, treatment: null }"), "sales.catalog.yaml");
        Assert.Equal(ColumnTreatment.Null, catalog.Access!.Columns.Single().Treatment);
    }

    [Fact]
    public void The_masks_that_cannot_be_masks_are_listed_with_the_reason_and_are_the_ones_the_view_hides()
    {
        // fact_budget.region is a column of an Analysis Services source; fact_sales.note (bound by nothing) has only a T-SQL expression, so a constant cannot be written around it; dim_customer.customer is text with a plain expression
        // (a mask of a column that is not text is refused when the catalog loads, DBD-ACC-008, so that case never reaches the view)
        var text = With("""
            access:
              columns:
                - { subject: interns, dataset: fact_budget, column: region, treatment: mask, mask: hash }
                - { subject: interns, dataset: fact_budget, column: region, treatment: mask }
                - { subject: interns, dataset: fact_sales, column: note, treatment: mask }
                - { subject: analysts, dataset: fact_sales, column: note, treatment: mask, mask: hash }
                - { subject: interns, dataset: dim_customer, column: customer, treatment: mask }
                - { subject: interns, dataset: dim_customer, column: region, treatment: "null" }
            """).Replace("      - {name: net_amount, datatype: Decimal, expression: {dialects: [{dialect: ANSI_SQL, expression: net_amount}]}}\n", "      - {name: net_amount, datatype: Decimal, expression: {dialects: [{dialect: ANSI_SQL, expression: net_amount}]}}\n      - {name: note, datatype: String, expression: {dialects: [{dialect: TSQL, expression: \"[note]\"}]}}\n", StringComparison.Ordinal);
        var catalog = CatalogLoader.LoadText(text, "sales.catalog.yaml");
        var hidden = CatalogColumnView.MasksThatHide(catalog, catalog.Access!);
        Assert.Equal(["fact_budget.region: its source is Analysis Services", "fact_sales.note: it has no plain SQL expression to mask"], hidden.Select(h => $"{h.Dataset}.{h.Column}: {h.Reason}").Order(StringComparer.Ordinal));

        // the view agrees: the text column with a plain expression stays a mask for interns, and the attribute bound to the column of the Analysis Services source is gone for them
        var view = CatalogColumnView.For(catalog, ColumnTreatments.For(catalog.Access, "u1", "u1", ["interns"]));
        Assert.Contains("customers.customer", view.Treated.Keys);
        Assert.DoesNotContain(view.Hidden, h => h == "customers.customer");
        Assert.Contains("customers.region", view.Hidden);
    }
}
