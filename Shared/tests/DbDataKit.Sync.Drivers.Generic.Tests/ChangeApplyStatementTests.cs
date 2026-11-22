using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>
/// The statements of the <c>ChangeApply</c> writer per engine, with no server: the collapse, the DELETE, the UPDATE in the engine's own join form (or the
/// correlated form for Oracle) and the INSERT that anti-joins on the key. The live runs on every engine are in each driver's own test project.
/// </summary>
public sealed class ChangeApplyStatementTests
{
    private static readonly List<CachedColumn> Columns =
    [
        new("Id", "int", false, true, false),
        new("Name", "nvarchar(50)", true, false, false),
        new("Qty", "int", true, false, false),
    ];

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "Id", TargetColumn = "Id" },
        new() { SourceColumn = "Name", TargetColumn = "Name" },
        new() { SourceColumn = "Qty", TargetColumn = "Qty" },
    ];

    private static ChangeApplyWriter.Statements Build(SqlDialect dialect, bool chunked = false, IReadOnlyList<CachedColumn>? columns = null, IReadOnlyList<ColumnMapping>? mappings = null)
    {
        var shape = TargetShape.FromCachedColumns(dialect, "orders", columns ?? Columns, new TableRef { ConnectionName = "t", Database = "db", Schema = "dbo", Table = "Orders" }, mappings ?? Mappings);
        return ChangeApplyWriter.Statements.Build(dialect, shape, ChangeApplyWriter.RequireKey(shape, "orders"), "dbo.STG", chunked);
    }

    [Fact]
    public void Sql_server_updates_with_a_join_and_inserts_what_the_target_lacks()
    {
        var s = Build(MsSqlDialect.Instance);
        Assert.Equal(
            "DELETE FROM dbo.STG\nWHERE [__Ordinal] NOT IN (SELECT last_seq FROM (SELECT MAX([__Ordinal]) AS last_seq FROM dbo.STG GROUP BY [Id]) latest)",
            s.Collapse.Text);
        Assert.Equal(
            "DELETE FROM [dbo].[Orders]\nWHERE EXISTS (SELECT 1 FROM dbo.STG src\n  WHERE src.[__Operation] = 'D'\n  AND src.[Id] = [dbo].[Orders].[Id])",
            s.Delete.Text);
        Assert.Equal(
            "UPDATE tgt SET tgt.[Name] = src.[Name], tgt.[Qty] = src.[Qty]\nFROM [dbo].[Orders] tgt\nJOIN dbo.STG src ON tgt.[Id] = src.[Id]\nWHERE src.[__Operation] <> 'D'",
            s.Update!.Text);
        Assert.Equal(
            "INSERT INTO [dbo].[Orders] ([Id], [Name], [Qty])\nSELECT src.[Id], src.[Name], src.[Qty] FROM dbo.STG src\nWHERE src.[__Operation] <> 'D'\n  AND NOT EXISTS (SELECT 1 FROM [dbo].[Orders] tgt WHERE tgt.[Id] = src.[Id])",
            s.Insert.Text);
    }

    [Fact]
    public void Postgres_updates_from_the_stage()
    {
        var s = Build(PostgresDialect.Instance);
        Assert.Equal(
            "UPDATE \"dbo\".\"Orders\" tgt SET \"Name\" = src.\"Name\", \"Qty\" = src.\"Qty\"\nFROM dbo.STG src\nWHERE tgt.\"Id\" = src.\"Id\"\n  AND src.\"__Operation\" <> 'D'",
            s.Update!.Text);
        Assert.StartsWith("INSERT INTO \"dbo\".\"Orders\" (\"Id\", \"Name\", \"Qty\")\nSELECT src.\"Id\"", s.Insert.Text);
    }

    [Fact]
    public void Mysql_updates_with_a_join_and_set()
    {
        var s = Build(MySqlDialect.Instance);
        Assert.Equal(
            "UPDATE `dbo`.`Orders` tgt\nJOIN dbo.STG src ON tgt.`Id` = src.`Id`\nSET tgt.`Name` = src.`Name`, tgt.`Qty` = src.`Qty`\nWHERE src.`__Operation` <> 'D'",
            s.Update!.Text);
        // MySQL refuses a DELETE whose subquery reads the table it deletes from: the group-by sits in a derived table
        Assert.Contains("NOT IN (SELECT last_seq FROM (SELECT MAX(", s.Collapse.Text);
    }

    [Fact]
    public void Oracle_updates_with_a_correlated_subquery_and_exists_never_a_merge()
    {
        var s = Build(OracleDialect.Instance);
        Assert.Equal(
            "UPDATE \"dbo\".\"Orders\" tgt\nSET (\"Name\", \"Qty\") = (SELECT src.\"Name\", src.\"Qty\" FROM dbo.STG src\n    WHERE src.\"Id\" = tgt.\"Id\"\n  AND src.\"__Operation\" <> 'D')\nWHERE EXISTS (SELECT 1 FROM dbo.STG src\n  WHERE src.\"Id\" = tgt.\"Id\"\n  AND src.\"__Operation\" <> 'D')",
            s.Update!.Text);
        foreach (var text in new[] { s.Collapse.Text, s.Delete.Text, s.Update.Text, s.Insert.Text })
            Assert.DoesNotContain("MERGE", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_single_value_column_is_set_without_a_row_constructor()
    {
        var columns = Columns.Take(2).ToList();
        var mappings = Mappings.Take(2).ToList();
        var text = Build(OracleDialect.Instance, columns: columns, mappings: mappings).Update!.Text;
        Assert.Contains("SET \"Name\" = (SELECT src.\"Name\" FROM", text);
    }

    [Fact]
    public void Every_statement_of_every_engine_is_free_of_values_and_of_the_upsert_forms()
    {
        foreach (var dialect in new SqlDialect[] { MsSqlDialect.Instance, PostgresDialect.Instance, MySqlDialect.Instance, OracleDialect.Instance })
        {
            var s = Build(dialect);
            foreach (var text in new[] { s.Collapse.Text, s.Delete.Text, s.Update!.Text, s.Insert.Text })
            {
                Assert.DoesNotContain("MERGE", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("ON CONFLICT", text, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("ON DUPLICATE", text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void A_chunked_apply_slices_the_stage_by_ordinal_in_every_statement_but_the_collapse()
    {
        var s = Build(PostgresDialect.Instance, chunked: true);
        Assert.Empty(s.Collapse.Slots);
        foreach (var statement in new[] { s.Delete, s.Update!, s.Insert })
        {
            Assert.Equal(["afterOrdinal", "upToOrdinal"], statement.Slots.Select(p => p.Name));
            Assert.Contains("src.\"__Ordinal\" > ", statement.Text);
        }
    }

    [Fact]
    public void A_table_with_no_primary_key_is_refused_naming_the_way_out()
    {
        var columns = Columns.Select(c => new CachedColumn(c.Name, c.NativeType, c.IsNullable, false, false)).ToList();
        var shape = TargetShape.FromCachedColumns(MsSqlDialect.Instance, "orders", columns, new TableRef { ConnectionName = "t", Database = "db", Schema = "dbo", Table = "Orders" }, Mappings);
        var ex = Assert.Throws<InvalidOperationException>(() => ChangeApplyWriter.RequireKey(shape, "orders"));
        Assert.Contains("no primary key", ex.Message);
        Assert.Contains("DeleteInsert", ex.Message);
    }

    [Fact]
    public void A_mapping_that_leaves_out_the_key_is_refused()
    {
        var shape = TargetShape.FromCachedColumns(MsSqlDialect.Instance, "orders", Columns, new TableRef { ConnectionName = "t", Database = "db", Schema = "dbo", Table = "Orders" }, Mappings.Skip(1).ToList());
        Assert.Contains("Id", Assert.Throws<InvalidOperationException>(() => ChangeApplyWriter.RequireKey(shape, "orders")).Message);
    }

    [Fact]
    public void A_generated_non_key_column_is_inserted_and_never_updated()
    {
        var columns = new List<CachedColumn> { new("Id", "int", false, true, false), new("Seq", "int", false, false, true), new("Name", "nvarchar(50)", true, false, false) };
        var mappings = columns.Select(c => new ColumnMapping { SourceColumn = c.Name, TargetColumn = c.Name }).ToList();
        var s = Build(PostgresDialect.Instance, columns: columns, mappings: mappings);
        Assert.DoesNotContain("\"Seq\"", s.Update!.Text);
        Assert.Contains("\"Seq\"", s.Insert.Text);
        Assert.Contains("OVERRIDING SYSTEM VALUE", s.Insert.Text);
    }

    [Fact]
    public void Writers_of_every_engine_driver_register_the_kind_and_it_does_not_reconcile()
    {
        var writer = new ChangeApplyWriter(MsSqlDialect.Instance, new InformationSchemaQueries(MsSqlDialect.Instance), new NoBinder());
        Assert.Equal("ChangeApply", writer.Kind);
        Assert.False(writer.SupportsReconciliation);
    }

    [Fact]
    public void Oracle_stages_a_null_as_a_literal_with_aliased_first_branch_columns()
    {
        // ORA-01790 (a NULL bound as text against a number in another UNION ALL branch) and ORA-00918 (two literal NULLs as one column name) are both avoided:
        // a delete of a change set carries NULL in every non-key column, so the staged rows of any pass with a delete and an insert hit them
        var rows = new List<object?[]> { new object?[] { 1, null, null, "D" }, new object?[] { 2, "x", 5m, "U" } };
        var text = StagingStatement.BuildInsert(OracleDialect.Instance, "\"S\".\"STG\"", ["ID", "NAME", "AMOUNT"], 2, rows: rows);
        Assert.Contains("SELECT :s0_0 AS c0, NULL AS c1, NULL AS c2, :s0_3 AS c3 FROM dual", text);
        Assert.Contains("UNION ALL SELECT :s1_0, :s1_1, :s1_2, :s1_3 FROM dual", text);
        Assert.DoesNotContain(":s0_1", text);
    }

    [Fact]
    public void Other_engines_keep_binding_every_cell()
    {
        var text = StagingStatement.BuildInsert(PostgresDialect.Instance, "stg", ["a", "b"], 1);
        Assert.Equal(StagingStatement.BuildInsert(PostgresDialect.Instance, "stg", ["a", "b"], 1, rows: null), text);
        Assert.False(PostgresDialect.Instance.InlineNullsInStagingInserts);
        Assert.False(MsSqlDialect.Instance.InlineNullsInStagingInserts);
        Assert.False(MySqlDialect.Instance.InlineNullsInStagingInserts);
        Assert.True(OracleDialect.Instance.InlineNullsInStagingInserts);
    }

    private sealed class NoBinder : ISegmentValueBinder
    {
        public System.Data.Common.DbParameter CreateParameter(string name, string rawValue, ColumnMetadata column) => throw new NotSupportedException();
    }
}
