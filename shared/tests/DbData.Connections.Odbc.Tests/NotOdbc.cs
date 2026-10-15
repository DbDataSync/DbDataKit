using System.Data;
using System.Data.Common;

namespace DbData.Connections.Odbc.Tests;

public sealed class NotOdbc : DbConnection
{
#pragma warning disable CS8765
    public override string ConnectionString { get; set; } = "";
#pragma warning restore CS8765
    public override string Database => "";
    public override string DataSource => "";
    public override string ServerVersion => "";
    public override ConnectionState State => ConnectionState.Open;
    public override void ChangeDatabase(string databaseName) { }
    public override void Close() { }
    public override void Open() { }
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
}
