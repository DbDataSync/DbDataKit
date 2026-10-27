using System.Data;
using System.Data.Common;

namespace DbDataKit.Metadata;

/// <summary>
/// A connection that is not one: for a reader made over no connection because its owner overrides how queries are run (Build runs the catalog queries through its read session, which guards and logs each one). Anything that reaches
/// for it fails with a message that says so.
/// </summary>
public sealed class UnusedConnection : DbConnection
{
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get => ""; set { } }
    public override string Database => "";
    public override string DataSource => "";
    public override string ServerVersion => "";
    public override ConnectionState State => ConnectionState.Closed;
    public override void ChangeDatabase(string databaseName) => throw Refused();
    public override void Close() { }
    public override void Open() => throw Refused();
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw Refused();
    protected override DbCommand CreateDbCommand() => throw Refused();
    private static NotSupportedException Refused() => new("This reader runs its queries through its owner, not through a connection of its own.");
}
