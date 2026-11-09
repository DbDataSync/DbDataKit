using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.TestSupport;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>The <c>ChangeApply</c> writer on SQL Server (UPDATE ... FROM ... JOIN), through the SqlBulkCopy staging provider into a temp table.</summary>
[Trait("Category", "Integration")]
public sealed class MsSqlChangeApplyTests(MsSqlTestDatabase db) : ChangeApplyTestsBase, IClassFixture<MsSqlTestDatabase>
{
    private static readonly MsSqlDriver Driver = new();

    protected override DbConnection OpenConnection() => db.OpenConnection();
    protected override IStagingProvider Staging { get; } = Driver.StagingProviders.Single(p => p.Kind == MsSqlDriverKinds.StagingTable);
    protected override IChangeWriter Writer { get; } = Driver.Writers.Single(w => w.Kind == "ChangeApply");
    protected override TableRef TargetRef(string table) => new() { ConnectionName = "t", Database = db.DatabaseName, Schema = "dbo", Table = table };
    protected override string IntType => "int";
    protected override string TextType => "nvarchar(100)";
    protected override string DecimalType => "decimal(18,2)";
    protected override string Quote(string identifier) => $"[{identifier}]";
    protected override string Qualified(string table) => $"dbo.[{table}]";
    protected override string DropSql(string table) => $"DROP TABLE dbo.[{table}]";
}

/// <summary>The same scenarios through the portable batched-INSERT staging provider into a real table.</summary>
[Trait("Category", "Integration")]
public sealed class MsSqlChangeApplyThroughBatchStagingTests(MsSqlTestDatabase db) : ChangeApplyTestsBase, IClassFixture<MsSqlTestDatabase>
{
    private static readonly MsSqlDriver Driver = new();

    protected override DbConnection OpenConnection() => db.OpenConnection();
    protected override IStagingProvider Staging { get; } = Driver.StagingProviders.Single(p => p.Kind == DbDataSync.Drivers.Generic.GenericDriverKinds.StagingTable);
    protected override IChangeWriter Writer { get; } = Driver.Writers.Single(w => w.Kind == "ChangeApply");
    protected override TableRef TargetRef(string table) => new() { ConnectionName = "t", Database = db.DatabaseName, Schema = "dbo", Table = table };
    protected override string IntType => "int";
    protected override string TextType => "nvarchar(100)";
    protected override string DecimalType => "decimal(18,2)";
    protected override string Quote(string identifier) => $"[{identifier}]";
    protected override string Qualified(string table) => $"dbo.[{table}]";
    protected override string DropSql(string table) => $"DROP TABLE dbo.[{table}]";
}
