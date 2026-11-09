using System.Text.Json;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>
/// How a decoded <c>wal2json</c> record becomes change rows, with no server: an update that changes the key is a delete of the old key plus an insert of the new row
/// (the way Change Tracking and CDC say it), every other record keeps its single row. This is what lets a writer that applies a key at a time (<c>ChangeApply</c>)
/// leave no stale row behind.
/// </summary>
public sealed class PgLogicalSlotKeyChangeTests
{
    private static readonly ChangeSchema Schema = new(["id", "name"]);
    private static readonly Dictionary<string, string> Types = new() { ["id"] = "integer", ["name"] = "text" };

    private static List<ChangeRow> Rows(string json)
    {
        using var document = JsonDocument.Parse(json);
        return PgLogicalSlotReader.ToChangeRows(document.RootElement, Schema, Types, "0/1", "public.t").ToList();
    }

    private static string Col(string name, string type, string value) => $$"""{"name":"{{name}}","type":"{{type}}","value":{{value}}}""";

    [Fact]
    public void An_update_that_changes_the_key_is_a_delete_of_the_old_key_and_an_insert_of_the_new_row()
    {
        var rows = Rows($$"""
            {"action":"U","schema":"public","table":"t","columns":[{{Col("id", "integer", "10")}},{{Col("name", "text", "\"a\"")}}],"identity":[{{Col("id", "integer", "1")}}]}
            """);

        Assert.Equal([ChangeOperation.Delete, ChangeOperation.Insert], rows.Select(r => r.Operation));
        Assert.Equal(1, rows[0]["id"]);
        Assert.Null(rows[0]["name"]);
        Assert.Equal(10, rows[1]["id"]);
        Assert.Equal("a", rows[1]["name"]);
    }

    [Fact]
    public void An_update_that_keeps_the_key_stays_one_update_with_the_new_values()
    {
        var rows = Rows($$"""
            {"action":"U","schema":"public","table":"t","columns":[{{Col("id", "integer", "1")}},{{Col("name", "text", "\"b\"")}}],"identity":[{{Col("id", "integer", "1")}}]}
            """);

        var row = Assert.Single(rows);
        Assert.Equal(ChangeOperation.Update, row.Operation);
        Assert.Equal("b", row["name"]);
    }

    [Fact]
    public void An_update_without_an_identity_stays_one_update()
    {
        var row = Assert.Single(Rows($$"""
            {"action":"U","columns":[{{Col("id", "integer", "1")}},{{Col("name", "text", "\"b\"")}}]}
            """));
        Assert.Equal(ChangeOperation.Update, row.Operation);
    }

    [Fact]
    public void A_delete_carries_its_key_and_nulls_elsewhere_and_an_insert_its_row()
    {
        var delete = Assert.Single(Rows($$"""{"action":"D","identity":[{{Col("id", "integer", "7")}}]}"""));
        Assert.Equal(ChangeOperation.Delete, delete.Operation);
        Assert.Equal(7, delete["id"]);
        Assert.Null(delete["name"]);

        var insert = Assert.Single(Rows($$"""{"action":"I","columns":[{{Col("id", "integer", "8")}},{{Col("name", "text", "null")}}]}"""));
        Assert.Equal(ChangeOperation.Insert, insert.Operation);
        Assert.Equal(8, insert["id"]);
        Assert.Null(insert["name"]);
    }

    [Fact]
    public void A_record_that_is_not_a_row_change_gives_no_row()
    {
        Assert.Empty(Rows("""{"action":"T","schema":"public","table":"t"}"""));
        Assert.Empty(Rows("""{"action":"M","prefix":"x"}"""));
    }
}
