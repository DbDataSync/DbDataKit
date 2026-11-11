using DbDataKit.Dialect;
using Xunit;

namespace DbDataKit.Transfer.Tests;

/// <summary>The <c>apply_staged_changes</c> strategy (CW01a): the collapse, DELETE, UPDATE and INSERT as text per engine, the statement order, the parameters and the refusals. Live runs: the conformance suites.</summary>
public class StagedChangesStatementTests
{
    private static readonly TransferSchema Schema = new(
    [
        new TransferColumn("id", CanonicalType.Of(CanonicalKind.Int32), Nullable: false, IsKey: true),
        new TransferColumn("name", CanonicalType.String(10)),
        new TransferColumn("qty", CanonicalType.Of(CanonicalKind.Int32)),
    ]);

    private static ApplyRequest Request(ScopedOptions? o = null, TransferSchema? schema = null) =>
        new(new TableName("sch", "tgt"), new TableName("sch", "stg"), schema ?? Schema) { Scoped = o ?? new ScopedOptions { Marker = new OperationMarker() } };

    private static StatementSet Build(string engine, ScopedOptions? o = null, TransferSchema? schema = null) =>
        StrategyCatalog.Get(StrategyNames.ApplyStagedChanges).Build(Request(o, schema), DialectCatalog.For(engine));

    [Fact]
    public void The_statements_run_in_one_transaction_in_the_order_collapse_delete_update_insert()
    {
        var set = Build("sqlserver");
        Assert.True(set.Transactional);
        Assert.Equal(["apply_staged_changes:collapse", "apply_staged_changes:delete", "apply_staged_changes:update", "apply_staged_changes:insert"], set.Statements.Select(s => s.Id));
        Assert.Equal([WriteKind.Delete, WriteKind.Delete, WriteKind.Update, WriteKind.Insert], set.Statements.Select(s => s.Intent.Kind));
        // the collapse works on the stage, not the target
        Assert.Equal("sch.stg", set.Statements[0].Intent.Target);
        Assert.All(set.Statements.Skip(1), s => Assert.Equal("sch.tgt", s.Intent.Target));
    }

    [Fact]
    public void Sql_server_takes_update_from_join()
    {
        var s = Build("sqlserver").Statements;
        Assert.Equal("DELETE FROM [sch].[stg]\nWHERE [__Ordinal] NOT IN (SELECT last_seq FROM (SELECT MAX([__Ordinal]) AS last_seq FROM [sch].[stg] GROUP BY [id]) latest);", s[0].Text);
        Assert.Equal("DELETE FROM [sch].[tgt]\nWHERE EXISTS (SELECT 1 FROM [sch].[stg] src\n  WHERE src.[__Operation] = 'D'\n  AND src.[id] = [sch].[tgt].[id]);", s[1].Text);
        Assert.Equal("UPDATE tgt SET tgt.[name] = src.[name], tgt.[qty] = src.[qty]\nFROM [sch].[tgt] tgt\nJOIN [sch].[stg] src ON tgt.[id] = src.[id]\nWHERE src.[__Operation] <> 'D';", s[2].Text);
        Assert.Equal("INSERT INTO [sch].[tgt] ([id], [name], [qty])\nSELECT src.[id], src.[name], src.[qty] FROM [sch].[stg] src\nWHERE src.[__Operation] <> 'D'\n  AND NOT EXISTS (SELECT 1 FROM [sch].[tgt] tgt WHERE tgt.[id] = src.[id]);", s[3].Text);
    }

    [Theory]
    [InlineData("postgres", "UPDATE \"sch\".\"tgt\" tgt SET \"name\" = src.\"name\", \"qty\" = src.\"qty\"\nFROM \"sch\".\"stg\" src\nWHERE tgt.\"id\" = src.\"id\"\n  AND src.\"__Operation\" <> 'D'")]
    [InlineData("duckdb", "UPDATE \"sch\".\"tgt\" tgt SET \"name\" = src.\"name\", \"qty\" = src.\"qty\"\nFROM \"sch\".\"stg\" src\nWHERE tgt.\"id\" = src.\"id\"\n  AND src.\"__Operation\" <> 'D'")]
    [InlineData("mysql", "UPDATE `sch`.`tgt` tgt\nJOIN `sch`.`stg` src ON tgt.`id` = src.`id`\nSET tgt.`name` = src.`name`, tgt.`qty` = src.`qty`\nWHERE src.`__Operation` <> 'D'")]
    [InlineData("oracle", "UPDATE \"sch\".\"tgt\" tgt\nSET (\"name\", \"qty\") = (SELECT src.\"name\", src.\"qty\" FROM \"sch\".\"stg\" src\n    WHERE src.\"id\" = tgt.\"id\"\n  AND src.\"__Operation\" <> 'D')\nWHERE EXISTS (SELECT 1 FROM \"sch\".\"stg\" src\n  WHERE src.\"id\" = tgt.\"id\"\n  AND src.\"__Operation\" <> 'D')")]
    public void The_update_is_spelled_in_the_engines_own_join_form(string engine, string expected) =>
        Assert.Equal(expected, Build(engine).Statements[2].Text);

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    [InlineData("mysql")]
    [InlineData("oracle")]
    [InlineData("duckdb")]
    public void No_engine_gets_a_merge_or_an_upsert_clause_and_no_value_is_in_the_text(string engine)
    {
        foreach (var s in Build(engine).Statements)
        {
            Assert.DoesNotContain("MERGE", s.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ON CONFLICT", s.Text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ON DUPLICATE", s.Text, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(s.Parameters);
        }
    }

    [Fact]
    public void An_ordinal_slice_limits_the_delete_update_and_insert_but_not_the_collapse()
    {
        var set = Build("postgres", new ScopedOptions { Marker = new OperationMarker(), Ordinal = new OrdinalRange(0, 100) });
        Assert.Empty(set.Statements[0].Parameters);
        foreach (var s in set.Statements.Skip(1))
        {
            Assert.Equal(["$1", "$2"], s.Parameters.Select(p => p.Marker));
            Assert.Equal(CanonicalKind.Int64, s.Parameters[0].Type.Kind);
            Assert.Contains("src.\"__Ordinal\" > $1", s.Text);
        }
    }

    [Fact]
    public void The_oracle_form_binds_the_slice_in_both_subqueries_when_a_style_numbers_its_parameters()
    {
        var set = Build("oracle", new ScopedOptions { Marker = new OperationMarker(), Ordinal = new OrdinalRange(0, 100) });
        Assert.Equal(2, set.Statements[2].Parameters.Count); // a named style binds a repeated name once
    }

    [Fact]
    public void A_table_of_key_columns_only_has_no_update()
    {
        var keysOnly = new TransferSchema([new TransferColumn("id", CanonicalType.Of(CanonicalKind.Int32), Nullable: false, IsKey: true)]);
        var set = Build("sqlserver", schema: keysOnly);
        Assert.Equal(["apply_staged_changes:collapse", "apply_staged_changes:delete", "apply_staged_changes:insert"], set.Statements.Select(s => s.Id));
    }

    [Fact]
    public void A_composite_key_is_grouped_and_matched_on_every_column()
    {
        var composite = new TransferSchema(
        [
            new TransferColumn("a", CanonicalType.Of(CanonicalKind.Int32), Nullable: false, IsKey: true),
            new TransferColumn("b", CanonicalType.Of(CanonicalKind.Int32), Nullable: false, IsKey: true),
            new TransferColumn("v", CanonicalType.String(10)),
        ]);
        var s = Build("postgres", schema: composite).Statements;
        Assert.Contains("GROUP BY \"a\", \"b\"", s[0].Text);
        Assert.Contains("src.\"a\" = \"sch\".\"tgt\".\"a\" AND src.\"b\" = \"sch\".\"tgt\".\"b\"", s[1].Text);
        Assert.Contains("tgt.\"a\" = src.\"a\" AND tgt.\"b\" = src.\"b\"", s[2].Text);
        Assert.DoesNotContain("\"a\" = src.\"a\",", s[2].Text);
    }

    [Fact]
    public void The_sequence_column_is_named_by_the_request_and_defaults_to_the_ordinal()
    {
        Assert.Contains("MAX(\"seq\")", Build("postgres", new ScopedOptions { Marker = new OperationMarker(), SequenceColumn = "seq" }).Statements[0].Text);
        Assert.Contains("MAX(\"__Ordinal\")", Build("postgres").Statements[0].Text);
    }

    [Fact]
    public void A_request_without_a_marker_means_the_stage_operation_column_and_one_without_a_key_is_refused()
    {
        Assert.Contains("src.\"__Operation\" = 'D'", Build("postgres", new ScopedOptions()).Statements[1].Text);
        var keyless = new TransferSchema([new TransferColumn("id", CanonicalType.Of(CanonicalKind.Int32)), new TransferColumn("name", CanonicalType.String(10))]);
        Assert.Throws<ArgumentException>(() => Build("postgres", schema: keyless));
    }

    [Fact]
    public void The_matrix_knows_the_strategy_on_every_engine_and_the_sync_kind_resolves_to_it()
    {
        foreach (var e in new[] { "duckdb", "sqlserver", "postgres", "mysql", "oracle", "fabric" })
            Assert.True(StrategyMatrix.Builtin.Check(StrategyNames.ApplyStagedChanges, new EngineId(e)).Allowed, e);
        Assert.Equal(MatrixStatus.Reference, StrategyMatrix.Builtin.Entry(StrategyNames.ApplyStagedChanges, EngineId.DuckDb)!.Status);
        Assert.Equal(StrategyNames.ApplyStagedChanges, StrategyMatrix.Builtin.Resolve("ChangeApply"));
    }

    [Fact]
    public void Update_styles_are_chosen_per_engine_and_a_callers_own_syntax_defaults_to_the_correlated_form()
    {
        Assert.Equal(UpdateStyle.FromJoin, StatementSyntax.UpdateStyleOf(EngineId.SqlServer));
        Assert.Equal(UpdateStyle.UpdateFrom, StatementSyntax.UpdateStyleOf(EngineId.Postgres));
        Assert.Equal(UpdateStyle.JoinSet, StatementSyntax.UpdateStyleOf(EngineId.MySql));
        Assert.Equal(UpdateStyle.Correlated, StatementSyntax.UpdateStyleOf(EngineId.Oracle));
        Assert.Equal(UpdateStyle.Correlated, new StatementSyntax { QuoteIdentifier = x => x, ParameterReference = (_, n) => n }.UpdateStyle);
    }
}
