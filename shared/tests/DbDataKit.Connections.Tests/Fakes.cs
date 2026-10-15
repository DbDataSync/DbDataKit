using System.Data;
using System.Data.Common;
using DbDataKit.Secrets;

namespace DbDataKit.Connections.Tests;

public sealed class FakeConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;
    public bool WasDisposed { get; private set; }
    public List<string> Executed { get; } = [];
#pragma warning disable CS8765
    public override string ConnectionString { get; set; } = "";
#pragma warning restore CS8765
    public override string Database => "";
    public override string DataSource => "";
    public override string ServerVersion => "1.0";
    public override ConnectionState State => _state;
    public override void ChangeDatabase(string databaseName) { }
    public override void Close() => _state = ConnectionState.Closed;
    public override void Open() => _state = ConnectionState.Open;
    public void Break() => _state = ConnectionState.Broken;
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => new FakeCommand(this);
    protected override void Dispose(bool disposing) { WasDisposed = true; _state = ConnectionState.Closed; base.Dispose(disposing); }
}

public sealed class FakeCommand(FakeConnection connection) : DbCommand
{
#pragma warning disable CS8765
    public override string CommandText { get; set; } = "";
#pragma warning restore CS8765
    public override int CommandTimeout { get; set; } = 30;
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; } = connection;
    protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
    protected override DbTransaction? DbTransaction { get; set; }
    public override void Cancel() { }
    public override int ExecuteNonQuery() { connection.Executed.Add(CommandText); return 0; }
    public override object? ExecuteScalar() { connection.Executed.Add(CommandText); return 1; }
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
}

public static class Env
{
    public static SecretResolverChain Chain(Dictionary<string, string> vars)
    {
        Func<string, string?> env = k => vars.TryGetValue(k, out var v) ? v : null;
        return new SecretResolverChain([new EnvSecretProvider(env), new LegacyEnvProvider(env)]);
    }
}
