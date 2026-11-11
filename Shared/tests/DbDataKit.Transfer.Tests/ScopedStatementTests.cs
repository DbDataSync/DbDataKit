using DbDataKit.Dialect;
using Xunit;

namespace DbDataKit.Transfer.Tests;

/// <summary>The scope-shaped builders (plan 13, A16a): rendered text, parameter slots, and the three decided design choices (structured scope, override style, caller callbacks).</summary>
public class ScopedStatementTests
{
    /// <summary>Sync's SQL Server writers' spelling: brackets, <c>@name</c>, bookkeeping columns bare, T-SQL MERGE.</summary>
    private static readonly StatementSyntax Bracket = new()
    {
        QuoteIdentifier = id => "[" + id.Replace("]", "]]") + "]",
        ParameterReference = (_, name) => "@" + name,
        QuoteBookkeepingColumns = false,
        TransactSql = true,
    };

    /// <summary>Sync's generic writers over an engine whose placeholders are <c>:name</c>.</summary>
    private static readonly StatementSyntax Colon = new()
    {
        QuoteIdentifier = id => "\"" + id.Replace("\"", "\"\"") + "\"",
        ParameterReference = (_, name) => ":" + name,
        ParameterStyle = ParameterStyle.NamedColon,
    };

    private static ScopedApplySpec Spec(Func<ScopedApplySpec, ScopedApplySpec>? with = null)
    {
        var s = new ScopedApplySpec
        {
            Target = SqlRelation.Raw("[dbo].[Orders]"),
            Stage = SqlRelation.Raw("#Staging_x"),
            Columns = ["Id", "Name", "Qty"],
            KeyColumns = ["Id"],
        };
        return with is null ? s : with(s);
    }

    // ---------------------------------------------------------------- scope

    [Fact]
    public void Every_row_scope_renders_a_tautology_without_parameters()
    {
        var r = ScopedStatements.RenderScope(Bracket, ScopeFilter.All);
        Assert.Equal("1 = 1", r.Predicate);
        Assert.Empty(r.Slots);
    }

    [Fact]
    public void A_list_scope_has_one_slot_per_value_in_order()
    {
        var r = ScopedStatements.RenderScope(Bracket, ScopeFilter.List("Region", ["east", "west", "north"]));
        Assert.Equal("[Region] IN (@seg0, @seg1, @seg2)", r.Predicate);
        Assert.Equal(["seg0", "seg1", "seg2"], r.Slots.Select(p => p.Name));
        Assert.Equal([0, 1, 2], r.Slots.Select(p => p.Position));
        Assert.All(r.Slots, p => { Assert.Equal(ParameterRole.ScopeValue, p.Role); Assert.Equal("Region", p.Column); });
        Assert.Equal(["east", "west", "north"], r.Slots.Select(p => p.Value));
    }

    [Fact]
    public void A_range_scope_is_half_open_and_has_a_min_and_a_max_slot()
    {
        var r = ScopedStatements.RenderScope(Bracket, ScopeFilter.Range("OrderId", "10", "20"));
        Assert.Equal("[OrderId] >= @segMin AND [OrderId] < @segMax", r.Predicate);
        Assert.Equal([ParameterRole.ScopeMin, ParameterRole.ScopeMax], r.Slots.Select(p => p.Role));
        Assert.Equal(["10", "20"], r.Slots.Select(p => p.Value));
    }

    [Fact]
    public void An_empty_list_is_refused_because_a_reconciling_write_would_empty_the_scope()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ScopedStatements.RenderScope(Bracket, ScopeFilter.List("Region", [])));
        Assert.Contains("no values", ex.Message);
    }

    [Fact]
    public void The_column_reference_is_the_callers_to_resolve()
    {
        var r = ScopedStatements.RenderScope(Bracket, ScopeFilter.Range("OrderId", 1, 2), c => $"base.[{c}]");
        Assert.Equal("base.[OrderId] >= @segMin AND base.[OrderId] < @segMax", r.Predicate);
    }

    [Fact]
    public void Values_never_reach_the_text()
    {
        var spec = Spec(s => s with { Scope = ScopeFilter.List("Region", ["SENTINEL_1", "SENTINEL_2"]), Ordinal = new OrdinalRange(111222, 333444), Marker = new OperationMarker() });
        foreach (var text in new[] { ScopedStatements.ScopeDelete(Bracket, spec).Text, ScopedStatements.Refill(Bracket, spec).Text, ScopedStatements.Merge(Bracket, spec with { DeleteUnmatchedInScope = true }).Text })
        {
            Assert.DoesNotContain("SENTINEL", text);
            Assert.DoesNotContain("111222", text);
            Assert.DoesNotContain("333444", text);
        }
    }

    // ---------------------------------------------------------------- scope delete and refill (DeleteInsertWriter, MsSqlDeleteInsertWriter)

    [Fact]
    public void Scope_delete_text_matches_the_writers_with_and_without_a_terminator()
    {
        var spec = Spec(s => s with { Scope = ScopeFilter.Range("OrderId", "1", "2") });
        Assert.Equal("DELETE FROM [dbo].[Orders] WHERE [OrderId] >= @segMin AND [OrderId] < @segMax;", ScopedStatements.ScopeDelete(Bracket, spec).Text);
        Assert.Equal("DELETE FROM [dbo].[Orders] WHERE [OrderId] >= @segMin AND [OrderId] < @segMax", ScopedStatements.ScopeDelete(Bracket with { TransactSql = false }, spec).Text);
    }

    [Fact]
    public void Refill_leaves_out_delete_marked_rows()
    {
        var spec = Spec(s => s with { Target = SqlRelation.Raw("[dbo].[Orders]"), Stage = SqlRelation.Raw("[dbo].[DS_STG_x]"), Columns = ["Id", "Name"], Marker = new OperationMarker() });
        Assert.Equal(
            """
            INSERT INTO [dbo].[Orders] ([Id], [Name])
            SELECT [Id], [Name] FROM [dbo].[DS_STG_x]
            WHERE [__Operation] <> 'D'
            """,
            ScopedStatements.Refill(Bracket with { TransactSql = false, QuoteBookkeepingColumns = true }, spec).Text);
    }

    [Fact]
    public void Refill_by_ordinal_slice_keeps_the_marker_filter_and_has_two_slots()
    {
        var spec = Spec(s => s with { Columns = ["Id", "Name"], Marker = new OperationMarker(), Ordinal = new OrdinalRange(5000, 10000) });
        var built = ScopedStatements.Refill(Bracket with { TransactSql = false, QuoteBookkeepingColumns = true }, spec);
        Assert.Equal(
            """
            INSERT INTO [dbo].[Orders] ([Id], [Name])
            SELECT [Id], [Name] FROM #Staging_x
            WHERE [__Operation] <> 'D'
              AND [__Ordinal] > @afterOrdinal
              AND [__Ordinal] <= @upToOrdinal
            """,
            built.Text);
        Assert.Equal([ParameterRole.OrdinalAfter, ParameterRole.OrdinalUpTo], built.Slots.Select(p => p.Role));
        Assert.Equal([5000L, 10000L], built.Slots.Select(p => p.Value));
    }

    [Fact]
    public void Refill_without_a_marker_has_no_delete_filter_and_a_slice_opens_the_where()
    {
        var plain = ScopedStatements.Refill(Bracket, Spec(s => s with { Columns = ["Id"] }));
        Assert.Equal("INSERT INTO [dbo].[Orders] ([Id])\nSELECT [Id] FROM #Staging_x;", plain.Text);
        var sliced = ScopedStatements.Refill(Bracket, Spec(s => s with { Columns = ["Id"], Ordinal = new OrdinalRange(0, 7) }));
        Assert.Equal("INSERT INTO [dbo].[Orders] ([Id])\nSELECT [Id] FROM #Staging_x\nWHERE __Ordinal > @afterOrdinal\n  AND __Ordinal <= @upToOrdinal;", sliced.Text);
    }

    [Fact]
    public void Refill_placeholders_come_from_the_callers_callback()
    {
        var built = ScopedStatements.Refill(Colon, Spec(s => s with { Ordinal = new OrdinalRange(0, 1) }));
        Assert.Contains(":afterOrdinal", built.Text);
        Assert.Contains(":upToOrdinal", built.Text);
        Assert.All(built.Slots, p => Assert.StartsWith(":", p.Marker));
    }

    [Fact]
    public void A_positional_syntax_binds_a_slot_per_occurrence_in_order()
    {
        var positional = new StatementSyntax { QuoteIdentifier = Bracket.QuoteIdentifier, ParameterReference = (_, _) => "?", ParameterStyle = ParameterStyle.Positional };
        var r = ScopedStatements.RenderScope(positional, ScopeFilter.List("a", [1, 2]));
        Assert.Equal("[a] IN (?, ?)", r.Predicate);
        Assert.Equal([0, 1], r.Slots.Select(p => p.Position));
    }

    [Fact]
    public void The_marker_value_is_a_literal_that_cannot_break_out_of_the_text()
    {
        var spec = Spec(s => s with { Marker = new OperationMarker("Op", "it's") });
        Assert.Contains("[Op] <> 'it''s'", ScopedStatements.Refill(Bracket with { QuoteBookkeepingColumns = true }, spec).Text);
    }

    // ---------------------------------------------------------------- generated-column override style

    [Fact]
    public void Override_none_asks_for_the_plain_head_and_needs_no_bracket()
    {
        var built = ScopedStatements.Refill(Bracket with { InsertInto = (t, c, o) => $"INSERT INTO {t} ({c}) /*{o}*/" }, Spec());
        Assert.StartsWith("INSERT INTO [dbo].[Orders] ([Id], [Name], [Qty]) /*False*/", built.Text);
        Assert.False(built.NeedsOverrideBracket);
    }

    [Fact]
    public void Override_clause_asks_the_caller_for_the_head_with_the_flag_set()
    {
        var syntax = Colon with { InsertInto = (t, c, o) => o ? $"INSERT INTO {t} ({c}) OVERRIDING SYSTEM VALUE" : $"INSERT INTO {t} ({c})" };
        var built = ScopedStatements.Refill(syntax, Spec(s => s with { Override = GeneratedColumnOverride.Clause }));
        Assert.StartsWith("INSERT INTO [dbo].[Orders] (\"Id\", \"Name\", \"Qty\") OVERRIDING SYSTEM VALUE\n", built.Text);
        Assert.False(built.NeedsOverrideBracket);
    }

    [Fact]
    public void Override_clause_without_a_caller_head_is_refused_not_guessed()
    {
        Assert.Throws<ArgumentException>(() => ScopedStatements.Refill(Colon, Spec(s => s with { Override = GeneratedColumnOverride.Clause })));
    }

    [Fact]
    public void Override_bracketed_returns_the_statement_for_the_caller_to_wrap()
    {
        var refill = ScopedStatements.Refill(Bracket, Spec(s => s with { Override = GeneratedColumnOverride.Bracketed }));
        var merge = ScopedStatements.Merge(Bracket, Spec(s => s with { Override = GeneratedColumnOverride.Bracketed }));
        Assert.True(refill.NeedsOverrideBracket);
        Assert.True(merge.NeedsOverrideBracket);
        Assert.False(ScopedStatements.ScopeDelete(Bracket, Spec(s => s with { Override = GeneratedColumnOverride.Bracketed })).NeedsOverrideBracket);
        Assert.DoesNotContain("IDENTITY_INSERT", refill.Text);
    }

    [Fact]
    public void A_merge_takes_no_clause_override()
    {
        Assert.Throws<ArgumentException>(() => ScopedStatements.Merge(Bracket, Spec(s => s with { Override = GeneratedColumnOverride.Clause })));
    }

    // ---------------------------------------------------------------- merge (MsSqlMergeWriter, MsSqlMergeReconcileWriter)

    [Fact]
    public void Merge_with_a_marker_matches_the_writers_text()
    {
        var spec = Spec(s => s with { Columns = ["Id", "Name"], Marker = new OperationMarker() });
        Assert.Equal(
            """
            MERGE INTO [dbo].[Orders] AS tgt
            USING #Staging_x AS src
            ON tgt.[Id] = src.[Id]
            WHEN MATCHED AND src.__Operation = 'D' THEN DELETE
            WHEN MATCHED AND src.__Operation <> 'D' THEN UPDATE SET tgt.[Name] = src.[Name]
            WHEN NOT MATCHED BY TARGET AND src.__Operation <> 'D'
                THEN INSERT ([Id], [Name]) VALUES (src.[Id], src.[Name]);
            """,
            ScopedStatements.Merge(Bracket, spec).Text);
    }

    [Fact]
    public void Merge_by_ordinal_slice_reads_the_stage_through_a_bounded_subquery()
    {
        var spec = Spec(s => s with { Columns = ["Id", "Name"], Marker = new OperationMarker(), Ordinal = new OrdinalRange(0, 5000) });
        var built = ScopedStatements.Merge(Bracket, spec);
        Assert.Equal(
            """
            MERGE INTO [dbo].[Orders] AS tgt
            USING (SELECT * FROM #Staging_x
                 WHERE __Ordinal > @afterOrdinal
                   AND __Ordinal <= @upToOrdinal) AS src
            ON tgt.[Id] = src.[Id]
            WHEN MATCHED AND src.__Operation = 'D' THEN DELETE
            WHEN MATCHED AND src.__Operation <> 'D' THEN UPDATE SET tgt.[Name] = src.[Name]
            WHEN NOT MATCHED BY TARGET AND src.__Operation <> 'D'
                THEN INSERT ([Id], [Name]) VALUES (src.[Id], src.[Name]);
            """,
            built.Text);
        Assert.Equal([ParameterRole.OrdinalAfter, ParameterRole.OrdinalUpTo], built.Slots.Select(p => p.Role));
    }

    [Fact]
    public void Merge_where_every_column_is_a_key_has_no_update_clause_but_keeps_the_blank_line()
    {
        var spec = Spec(s => s with { Columns = ["Id"], Marker = new OperationMarker() });
        Assert.Equal(
            """
            MERGE INTO [dbo].[Orders] AS tgt
            USING #Staging_x AS src
            ON tgt.[Id] = src.[Id]
            WHEN MATCHED AND src.__Operation = 'D' THEN DELETE

            WHEN NOT MATCHED BY TARGET AND src.__Operation <> 'D'
                THEN INSERT ([Id]) VALUES (src.[Id]);
            """,
            ScopedStatements.Merge(Bracket, spec).Text);
    }

    [Fact]
    public void Merge_that_deletes_unmatched_rows_filters_the_target_in_a_cte_before_matching()
    {
        var spec = Spec(s => s with
        {
            Columns = ["Id", "Name"], Marker = new OperationMarker(), DeleteUnmatchedInScope = true,
            Scope = ScopeFilter.Range("Id", "1", "100"),
        });
        var built = ScopedStatements.Merge(Bracket, spec);
        Assert.Equal(
            """
            ;WITH TargetScope AS (
                SELECT * FROM [dbo].[Orders] WHERE [Id] >= @segMin AND [Id] < @segMax
            )
            MERGE INTO TargetScope AS tgt
            USING #Staging_x AS src
            ON tgt.[Id] = src.[Id]
            WHEN MATCHED AND src.__Operation = 'D' THEN DELETE
            WHEN MATCHED AND src.__Operation <> 'D' THEN UPDATE SET tgt.[Name] = src.[Name]
            WHEN NOT MATCHED BY TARGET AND src.__Operation <> 'D'
                THEN INSERT ([Id], [Name]) VALUES (src.[Id], src.[Name])
            WHEN NOT MATCHED BY SOURCE THEN DELETE;
            """,
            built.Text);
        Assert.Equal(["segMin", "segMax"], built.Slots.Select(p => p.Name));
    }

    [Fact]
    public void An_unsegmented_reconcile_scopes_to_the_whole_table()
    {
        var built = ScopedStatements.Merge(Bracket, Spec(s => s with { Marker = new OperationMarker(), DeleteUnmatchedInScope = true }));
        Assert.Contains("SELECT * FROM [dbo].[Orders] WHERE 1 = 1", built.Text);
        Assert.Empty(built.Slots);
    }

    [Fact]
    public void Delete_unmatched_is_transact_sql_only_because_a_standard_merge_target_cannot_be_a_cte()
    {
        Assert.Throws<ArgumentException>(() => ScopedStatements.Merge(Colon, Spec(s => s with { DeleteUnmatchedInScope = true })));
    }

    [Fact]
    public void The_standard_merge_spelling_has_no_by_target_and_unqualified_update_targets()
    {
        var text = ScopedStatements.Merge(Colon, Spec(s => s with { Columns = ["Id", "Name"], Marker = new OperationMarker() })).Text;
        Assert.Contains("UPDATE SET \"Name\" = src.\"Name\"", text);
        Assert.Contains("WHEN NOT MATCHED AND src.\"__Operation\" <> 'D'", text);
        Assert.DoesNotContain("BY TARGET", text);
        Assert.False(text.EndsWith(';'));
    }

    [Fact]
    public void A_merge_needs_a_key()
    {
        Assert.Throws<ArgumentException>(() => ScopedStatements.Merge(Bracket, Spec(s => s with { KeyColumns = [] })));
    }

    [Fact]
    public void Two_key_columns_join_on_both_in_the_order_given()
    {
        var text = ScopedStatements.Merge(Bracket, Spec(s => s with { KeyColumns = ["B", "A"], Columns = ["A", "B", "V"] })).Text;
        Assert.Contains("ON tgt.[B] = src.[B] AND tgt.[A] = src.[A]", text);
        Assert.Contains("UPDATE SET tgt.[V] = src.[V]", text);
    }

    // ---------------------------------------------------------------- the shared-dialect way in still works, and callbacks are an addition

    [Theory]
    [InlineData("sqlserver")]
    [InlineData("postgres")]
    public void From_a_dialect_quotes_and_marks_exactly_as_the_dialect_does(string engine)
    {
        var d = DialectCatalog.For(engine);
        var syntax = StatementSyntax.From(d);
        Assert.Equal(d.Quote("a]b"), syntax.QuoteIdentifier("a]b"));
        Assert.Equal(d.ParameterName(1, "x"), syntax.ParameterReference(1, "x"));
        var set = StrategyCatalog.Get(StrategyNames.DeleteInsertByScope).Build(Request(Scope(ScopeFilter.Range("id", 1, 5))), d);
        Assert.Equal(2, set.Statements.Count);
        Assert.Equal(d.Id == EngineId.SqlServer ? ["@segMin", "@segMax"] : ["$1", "$2"], set.Statements[0].Parameters.Select(p => p.Marker));
        Assert.Equal(CanonicalKind.Int32, set.Statements[0].Parameters[0].Type.Kind);
    }

    [Fact]
    public void A_request_with_callbacks_uses_them_instead_of_the_dialect()
    {
        var request = Request(new ScopedOptions { Scope = ScopeFilter.Range("id", 1, 5), Syntax = Colon });
        var set = StrategyCatalog.Get(StrategyNames.DeleteInsertByScope).Build(request, DialectCatalog.For(EngineId.SqlServer));
        Assert.Equal("DELETE FROM \"sch\".\"tgt\" WHERE \"id\" >= :segMin AND \"id\" < :segMax", set.Statements[0].Text);
        Assert.Equal([":segMin", ":segMax"], set.Statements[0].Parameters.Select(p => p.Marker));
    }

    [Fact]
    public void The_merge_strategy_refuses_delete_unmatched_off_sql_server()
    {
        var request = Request(new ScopedOptions { DeleteUnmatchedInScope = true });
        Assert.Throws<StrategyNotSupportedException>(() => StrategyCatalog.Get(StrategyNames.MergeStagedChanges).Build(request, DialectCatalog.For(EngineId.Postgres)));
        Assert.Contains("WHEN NOT MATCHED BY SOURCE", StrategyCatalog.Get(StrategyNames.MergeStagedChanges).Build(request, DialectCatalog.For(EngineId.SqlServer)).Statements.Single().Text);
    }

    [Fact]
    public void The_new_strategies_are_matrix_checked_like_the_others()
    {
        Assert.False(StrategyMatrix.Builtin.Check(StrategyNames.MergeStagedChanges, EngineId.Postgres, new EngineVersion(14)).Allowed);
        Assert.True(StrategyMatrix.Builtin.Check(StrategyNames.MergeStagedChanges, EngineId.Postgres, new EngineVersion(15)).Allowed);
        Assert.False(StrategyMatrix.Builtin.Check(StrategyNames.MergeStagedChanges, EngineId.MySql).Allowed);
        Assert.True(StrategyMatrix.Builtin.Check(StrategyNames.DeleteInsertByScope, EngineId.MySql).Allowed);
        Assert.Equal(StrategyNames.MergeStagedChanges, StrategyMatrix.Builtin.Resolve("MsSqlMergeReconcile"));
        Assert.Equal(StrategyNames.DeleteInsertByScope, StrategyMatrix.Builtin.Resolve("MsSqlDeleteInsert"));
    }

    private static ScopedOptions Scope(ScopeFilter scope) => new() { Scope = scope };

    private static ApplyRequest Request(ScopedOptions o) => new(
        new TableName("sch", "tgt"), new TableName("trk", "stg"),
        new TransferSchema([new TransferColumn("id", CanonicalType.Of(CanonicalKind.Int32), IsKey: true), new TransferColumn("name", CanonicalType.String(10))]))
    { Scoped = o };
}
