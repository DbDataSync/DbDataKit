using DbDataKit.Transfer;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// The statements of the three SQL Server writers, pinned as text with no server. They come from the shared builders
/// (<see cref="ScopedStatements"/>, plan 13, A16b) and must read exactly as the writers' own templates did before: brackets,
/// <c>@name</c>, the staging table's bookkeeping columns bare, a trailing semicolon and the blank line where an empty
/// update clause used to be. A difference here is a regression to explain, not an expectation to update.
/// </summary>
public sealed class MsSqlWriterStatementTests
{
    private static readonly List<CachedColumn> Columns =
    [
        new("Id", "int", false, true, false),
        new("Region", "nvarchar(20)", true, false, false),
        new("Amount", "decimal(18,2)", false, false, false),
    ];

    private static TableRef Target() => new() { ConnectionName = "tgt", Database = "db", Schema = "dbo", Table = "Orders" };

    private static MsSqlTargetShape Shape(params string[] mapped) => MsSqlTargetShape.FromCachedColumns(
        "orders", Columns, Target(), [.. mapped.Select(c => new ColumnMapping { SourceColumn = c, TargetColumn = c })]);

    [Fact]
    public void Merge_ReadsTheStagedSetDirectly()
    {
        Assert.Equal(
            """
            MERGE INTO [dbo].[Orders] AS tgt
            USING #Staging_x AS src
            ON tgt.[Id] = src.[Id]
            WHEN MATCHED AND src.__Operation = 'D' THEN DELETE
            WHEN MATCHED AND src.__Operation <> 'D' THEN UPDATE SET tgt.[Region] = src.[Region], tgt.[Amount] = src.[Amount]
            WHEN NOT MATCHED BY TARGET AND src.__Operation <> 'D'
                THEN INSERT ([Id], [Region], [Amount]) VALUES (src.[Id], src.[Region], src.[Amount]);
            """,
            MsSqlMergeWriter.BuildMerge(Shape("Id", "Region", "Amount"), "#Staging_x").Text);
    }

    [Fact]
    public void Merge_Chunked_ReadsOneOrdinalSliceThroughABoundedSubquery()
    {
        var built = MsSqlMergeWriter.BuildMerge(Shape("Id", "Amount"), "#Staging_x", chunked: true);

        Assert.Equal(
            """
            MERGE INTO [dbo].[Orders] AS tgt
            USING (SELECT * FROM #Staging_x
                 WHERE __Ordinal > @afterOrdinal
                   AND __Ordinal <= @upToOrdinal) AS src
            ON tgt.[Id] = src.[Id]
            WHEN MATCHED AND src.__Operation = 'D' THEN DELETE
            WHEN MATCHED AND src.__Operation <> 'D' THEN UPDATE SET tgt.[Amount] = src.[Amount]
            WHEN NOT MATCHED BY TARGET AND src.__Operation <> 'D'
                THEN INSERT ([Id], [Amount]) VALUES (src.[Id], src.[Amount]);
            """,
            built.Text);
        Assert.Equal(["afterOrdinal", "upToOrdinal"], built.Slots.Select(s => s.Name));
    }

    [Fact]
    public void Merge_WhereEveryMappedColumnIsTheKey_HasNoUpdateClauseButKeepsItsLine()
    {
        Assert.Equal(
            "MERGE INTO [dbo].[Orders] AS tgt\nUSING #Staging_x AS src\nON tgt.[Id] = src.[Id]\n" +
            "WHEN MATCHED AND src.__Operation = 'D' THEN DELETE\n\n" +
            "WHEN NOT MATCHED BY TARGET AND src.__Operation <> 'D'\n    THEN INSERT ([Id]) VALUES (src.[Id]);",
            MsSqlMergeWriter.BuildMerge(Shape("Id"), "#Staging_x").Text);
    }

    [Fact]
    public void Merge_WithoutAMappedKey_ExplainsWhatIsMissing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => MsSqlMergeWriter.BuildMerge(Shape("Amount"), "#Staging_x"));
        Assert.Equal("Column mappings must include the target primary key column(s): Id.", ex.Message);
    }

    [Fact]
    public void Reconcile_FiltersTheTargetInACteBeforeMatching()
    {
        var shape = Shape("Id", "Amount");
        var scope = SegmentScope.Resolve(new RangeSegment("Id", "1", "100"), shape.Columns);

        var built = MsSqlMergeReconcileWriter.BuildMerge(shape, scope.Filter, "#Staging_x");

        Assert.Equal(
            """
            ;WITH TargetScope AS (
                SELECT * FROM [dbo].[Orders] WHERE [Id] >= @segMin AND [Id] < @segMax
            )
            MERGE INTO TargetScope AS tgt
            USING #Staging_x AS src
            ON tgt.[Id] = src.[Id]
            WHEN MATCHED AND src.__Operation = 'D' THEN DELETE
            WHEN MATCHED AND src.__Operation <> 'D' THEN UPDATE SET tgt.[Amount] = src.[Amount]
            WHEN NOT MATCHED BY TARGET AND src.__Operation <> 'D'
                THEN INSERT ([Id], [Amount]) VALUES (src.[Id], src.[Amount])
            WHEN NOT MATCHED BY SOURCE THEN DELETE;
            """,
            built.Text);
        Assert.Equal(["segMin", "segMax"], built.Slots.Select(s => s.Name));
    }

    [Fact]
    public void Reconcile_Unsegmented_ScopesToTheWholeTable()
    {
        var built = MsSqlMergeReconcileWriter.BuildMerge(Shape("Id", "Amount"), ScopeFilter.All, "#Staging_x");

        Assert.Contains("SELECT * FROM [dbo].[Orders] WHERE 1 = 1", built.Text);
        Assert.Empty(built.Slots);
    }

    [Fact]
    public void DeleteInsert_EmptiesTheScopeWithATerminator()
    {
        var shape = Shape("Id", "Amount");
        var scope = SegmentScope.Resolve(new ListSegment("Region", ["EU", "US"]), shape.Columns);

        var built = MsSqlDeleteInsertWriter.BuildDelete(shape, scope.Filter);

        Assert.Equal("DELETE FROM [dbo].[Orders] WHERE [Region] IN (@seg0, @seg1);", built.Text);
        Assert.Equal(["seg0", "seg1"], built.Slots.Select(s => s.Name));
        Assert.Equal("DELETE FROM [dbo].[Orders] WHERE 1 = 1;", MsSqlDeleteInsertWriter.BuildDelete(shape, ScopeFilter.All).Text);
    }

    [Fact]
    public void DeleteInsert_RefillsWithoutStagedDeletes()
    {
        Assert.Equal(
            """
            INSERT INTO [dbo].[Orders] ([Id], [Amount])
            SELECT [Id], [Amount] FROM #Staging_x
            WHERE __Operation <> 'D';
            """,
            MsSqlDeleteInsertWriter.BuildInsert(Shape("Id", "Amount"), "#Staging_x").Text);
    }

    [Fact]
    public void AnIdentityColumn_IsABracketedWriteTheCallerWraps()
    {
        List<CachedColumn> identity = [new("Id", "int", false, true, true), new("Amount", "decimal(18,2)", false, false, false)];
        var shape = MsSqlTargetShape.FromCachedColumns("orders", identity, Target(),
            [new ColumnMapping { SourceColumn = "Id", TargetColumn = "Id" }, new ColumnMapping { SourceColumn = "Amount", TargetColumn = "Amount" }]);

        Assert.True(MsSqlMergeWriter.BuildMerge(shape, "#Staging_x").NeedsOverrideBracket);
        Assert.True(MsSqlDeleteInsertWriter.BuildInsert(shape, "#Staging_x").NeedsOverrideBracket);
        Assert.DoesNotContain("IDENTITY_INSERT", MsSqlDeleteInsertWriter.BuildInsert(shape, "#Staging_x").Text);
        Assert.False(MsSqlDeleteInsertWriter.BuildDelete(shape, ScopeFilter.All).NeedsOverrideBracket);
        Assert.False(MsSqlMergeWriter.BuildMerge(Shape("Id", "Amount"), "#Staging_x").NeedsOverrideBracket);
    }
}
