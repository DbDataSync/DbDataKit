using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.TestSupport;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>The <c>ChangeApply</c> writer on PostgreSQL (UPDATE ... FROM), through the binary COPY staging provider.</summary>
[Trait("Category", "Integration")]
public sealed class PostgresChangeApplyTests(PostgresTestDatabase db) : ChangeApplyTestsBase, IClassFixture<PostgresTestDatabase>
{
    private static readonly PostgresDriver Driver = new();

    protected override DbConnection OpenConnection() => db.OpenConnection();
    protected override IStagingProvider Staging { get; } = Driver.StagingProviders.Single(p => p.Kind == PostgresDriverKinds.CopyStaging);
    protected override IChangeWriter Writer { get; } = Driver.Writers.Single(w => w.Kind == GenericDriverKinds.ChangeApply);
    protected override TableRef TargetRef(string table) => new() { ConnectionName = "t", Database = db.DatabaseName, Schema = "public", Table = table };
    protected override string IntType => "integer";
    protected override string TextType => "varchar(100)";
    protected override string DecimalType => "numeric(18,2)";
    protected override string Qualified(string table) => $"public.\"{table}\"";
    protected override string DropSql(string table) => $"DROP TABLE public.\"{table}\"";
}

/// <summary>The same scenarios through the portable batched-INSERT staging provider.</summary>
[Trait("Category", "Integration")]
public sealed class PostgresChangeApplyThroughBatchStagingTests(PostgresTestDatabase db) : ChangeApplyTestsBase, IClassFixture<PostgresTestDatabase>
{
    private static readonly PostgresDriver Driver = new();

    protected override DbConnection OpenConnection() => db.OpenConnection();
    protected override IStagingProvider Staging { get; } = Driver.StagingProviders.Single(p => p.Kind == GenericDriverKinds.StagingTable);
    protected override IChangeWriter Writer { get; } = Driver.Writers.Single(w => w.Kind == GenericDriverKinds.ChangeApply);
    protected override TableRef TargetRef(string table) => new() { ConnectionName = "t", Database = db.DatabaseName, Schema = "public", Table = table };
    protected override string IntType => "integer";
    protected override string TextType => "varchar(100)";
    protected override string DecimalType => "numeric(18,2)";
    protected override string Qualified(string table) => $"public.\"{table}\"";
    protected override string DropSql(string table) => $"DROP TABLE public.\"{table}\"";
}
