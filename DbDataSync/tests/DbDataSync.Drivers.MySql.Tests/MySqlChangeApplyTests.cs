using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.TestSupport;

namespace DbDataSync.Drivers.MySql.Tests;

/// <summary>
/// The <c>ChangeApply</c> writer on the MySQL family (UPDATE ... JOIN ... SET), one body for MySQL and MariaDB. MariaDB takes the same text: its
/// single-table DELETE has no alias, and both refuse a DELETE that reads its own table, hence the derived table in the collapse.
/// </summary>
[Trait("Category", "Integration")]
public abstract class MySqlFamilyChangeApplyTestsBase(MySqlFamilyTestDatabase db) : ChangeApplyTestsBase
{
    private static readonly MySqlDriver Driver = new();

    protected override DbConnection OpenConnection() => db.OpenConnection();
    protected override IStagingProvider Staging { get; } = Driver.StagingProviders.Single(p => p.Kind == GenericDriverKinds.StagingTable);
    protected override IChangeWriter Writer { get; } = Driver.Writers.Single(w => w.Kind == GenericDriverKinds.ChangeApply);
    protected override TableRef TargetRef(string table) => new() { ConnectionName = "t", Database = db.DatabaseName, Schema = db.DatabaseName, Table = table };
    protected override string IntType => "int";
    protected override string TextType => "varchar(100)";
    protected override string DecimalType => "decimal(18,2)";
    protected override string Quote(string identifier) => $"`{identifier}`";
}

public sealed class MySqlChangeApplyTests(MySqlTestDatabase db) : MySqlFamilyChangeApplyTestsBase(db), IClassFixture<MySqlTestDatabase>;

public sealed class MariaDbChangeApplyTests(MariaDbTestDatabase db) : MySqlFamilyChangeApplyTestsBase(db), IClassFixture<MariaDbTestDatabase>;
