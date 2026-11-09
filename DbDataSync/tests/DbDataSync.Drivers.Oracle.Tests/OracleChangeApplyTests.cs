using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.TestSupport;

namespace DbDataSync.Drivers.Oracle.Tests;

/// <summary>The <c>ChangeApply</c> writer on Oracle: a correlated UPDATE with EXISTS, no MERGE.</summary>
[Trait("Category", "Integration")]
public sealed class OracleChangeApplyTests(OracleTestDatabase db) : ChangeApplyTestsBase, IClassFixture<OracleTestDatabase>
{
    private static readonly OracleDriver Driver = new();

    protected override DbConnection OpenConnection() => db.OpenConnection();
    protected override IStagingProvider Staging { get; } = Driver.StagingProviders.Single(p => p.Kind == GenericDriverKinds.StagingTable);
    protected override IChangeWriter Writer { get; } = Driver.Writers.Single(w => w.Kind == GenericDriverKinds.ChangeApply);
    protected override TableRef TargetRef(string table) => new() { ConnectionName = "t", Database = "FREEPDB1", Schema = db.SchemaName, Table = table };
    protected override string IntType => "NUMBER(10,0)";
    protected override string TextType => "VARCHAR2(100)";
    protected override string DecimalType => "NUMBER(18,2)";
    protected override string N(string name) => name.ToUpperInvariant();
    protected override string DropSql(string table) => $"DROP TABLE \"{table}\" PURGE";
}
