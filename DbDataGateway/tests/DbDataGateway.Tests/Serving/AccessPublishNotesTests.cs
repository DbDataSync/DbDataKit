using DbDataKit.Semantic;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Tests.Serving;

/// <summary>Plan 22a GP13 (owner decision 2026-11-12): the publish answer of a semantic model names every mask that became a hide, in short exact words: the column, the dataset, the reason.</summary>
public sealed class AccessPublishNotesTests
{
    private const string Catalog = """
        version: "0.3"
        name: money
        timezone: UTC
        sources:
          warehouse: {kind: sqlserver, dialects: [TSQL, DUCKDB]}
          cube: {kind: ssas, dialects: [DAX, DUCKDB]}
        tables:
          - name: fact
            x_source: warehouse
            source: fact
            columns:
              - {name: id, datatype: Integer, expression: {dialects: [{dialect: ANSI_SQL, expression: id}]}}
              - {name: amount, datatype: Decimal, expression: {dialects: [{dialect: ANSI_SQL, expression: amount}]}}
              - {name: note, datatype: String, expression: {dialects: [{dialect: TSQL, expression: "[note]"}]}}
              - {name: customer, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: customer}]}}
          - name: budget
            x_source: cube
            source: budget
            columns:
              - {name: region, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: region}, {dialect: DAX, expression: "'Budget'[Region]"}]}}
        dimensions:
          customers:
            attributes:
              customer:
                type: string
                bindings:
                  warehouse: {table: fact, column: customer}
        measures:
          fact.rows:
            type: integer
            source: warehouse
            agg: count
            of: {table: fact, column: id}
            semantics: {grain: [customers.customer], rollup: {default: none}, spread: blank, status: governed}
        access:
          columns:
            - { subject: interns, dataset: fact, column: note, treatment: mask }
            - { subject: interns, dataset: fact, column: customer, treatment: mask }
            - { subject: interns, dataset: budget, column: region, treatment: mask, mask: hash }
        """;

    [Fact]
    public void A_mask_on_a_column_of_an_Analysis_Services_source_or_with_no_plain_SQL_expression_is_named_as_a_hide_in_the_warnings()
    {
        var catalog = CatalogLoader.LoadText(Catalog, "money.catalog.yaml");
        var warnings = AccessPublishNotes.Warnings(catalog.Access, readOnDataSource: true, catalog)!.Select(n => (string?)n).ToList();
        Assert.Equal(
        [
            "The mask on 'note' of 'fact' hides the column instead: it has no plain SQL expression to mask.",
            "The mask on 'region' of 'budget' hides the column instead: its source is Analysis Services.",
        ], warnings.Order(StringComparer.Ordinal));
        Assert.DoesNotContain(warnings, w => w!.Contains("'customer'", StringComparison.Ordinal));   // a mask of text stays a mask
    }

    [Fact]
    public void A_publish_without_the_catalog_or_with_masks_that_hold_has_no_such_warning()
    {
        var catalog = CatalogLoader.LoadText(Catalog.Replace("column: note, treatment: mask }", "column: note, treatment: hide }", StringComparison.Ordinal)
            .Replace("dataset: budget, column: region, treatment: mask, mask: hash", "dataset: budget, column: region, treatment: hide", StringComparison.Ordinal), "money.catalog.yaml");
        Assert.Null(AccessPublishNotes.Warnings(catalog.Access, readOnDataSource: true, catalog));
        Assert.Null(AccessPublishNotes.Warnings(CatalogLoader.LoadText(Catalog, "money.catalog.yaml").Access, readOnDataSource: true));   // the caller that has no catalog to read says nothing about types
    }

    private const string StoreModel = """
        schemaVersion: 2
        name: mdm
        storage: {connection: wh}
        access:
          row:
            - dataset: territory
              groups:
                - id: g
                  rules:
                    - { id: r1, effect: include, table: rules.region_access, subject: who, columns: { region: region_code } }
        entities:
          territory:
            key: [code]
            attributes:
              code: {type: VARCHAR(10)}
              region: {type: VARCHAR(20)}
        """;

    private const string ReadsTerritory = """
        version: "0.3"
        name: reader
        timezone: UTC
        sources:
          warehouse: {kind: sqlserver, dialects: [TSQL, DUCKDB]}
        tables:
          - name: territory
            x_source: warehouse
            source: mdm.territory
            columns:
              - {name: code, datatype: String, expression: {dialects: [{dialect: ANSI_SQL, expression: code}]}}
        dimensions:
          places:
            attributes:
              code:
                type: string
                bindings:
                  warehouse: {table: territory, column: code}
        measures:
          territory.rows:
            type: integer
            source: warehouse
            agg: count
            of: {table: territory, column: code}
            semantics: {grain: [places.code], rollup: {default: none}, spread: blank, status: governed}
        """;

    [Fact]
    public void A_semantic_model_reading_a_Store_entity_table_with_rules_is_warned_once_and_not_when_the_model_is_gone_or_the_source_differs()
    {
        var store = new ServedEntityModel(DbDataKit.DataModel.DataModelLoader.Load(StoreModel, "mdm.yaml").Model!, "wh", 1, null);
        var served = new List<ServedEntityModel> { store };
        var ruled = new RuledTables(() => new DbDataKit.Serving.Semantic.SemanticModels(), () => served);
        var catalog = CatalogLoader.LoadText(ReadsTerritory, "reader.catalog.yaml");

        var warnings = AccessPublishNotes.ForSemantic(catalog, new Dictionary<string, string> { ["warehouse"] = "wh" }, ruled)!.Select(n => (string?)n).ToList();
        Assert.Equal(["Model 'mdm' puts access rules on 'mdm.territory' of data source 'wh'; this model reads the same table and does not apply them."], warnings);

        Assert.Null(AccessPublishNotes.ForSemantic(catalog, new Dictionary<string, string> { ["warehouse"] = "other" }, ruled));   // another data source is another table
        served.Clear();
        Assert.Null(AccessPublishNotes.ForSemantic(catalog, new Dictionary<string, string> { ["warehouse"] = "wh" }, ruled));       // the model with the rules is gone
    }
}
