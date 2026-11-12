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
}
