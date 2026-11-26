using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace DbDataKit.State.Tests;

/// <summary>
/// The migration lock of SQL Server and PostgreSQL belongs to the session. When the session ends while the lock is held (the server ends it after a severe error), the
/// connection is closed and the lock is gone with it: releasing it does nothing, so the error that ended the session is the one the caller sees.
/// </summary>
public sealed class MigrationLockReleaseTests
{
    public static TheoryData<string> Dialects => ["mssql", "postgres"];

    private static StateDialect Dialect(string name) => name == "mssql"
        ? new MsSqlStateDialect(_ => throw new InvalidOperationException("no provider in this test"))
        : new PostgresStateDialect(_ => throw new InvalidOperationException("no provider in this test"));

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Releasing_on_an_open_connection_runs_the_release(string name)
    {
        var connection = new RecordingConnection();
        connection.Open();

        Dialect(name).AcquireMigrationLock(connection)!.Dispose();

        Assert.Equal(2, connection.Executed.Count);
        Assert.Contains(name == "mssql" ? "sp_releaseapplock" : "pg_advisory_unlock", connection.Executed[1]);
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Releasing_on_a_closed_connection_does_nothing_and_the_error_that_closed_it_is_the_one_reported(string name)
    {
        var connection = new RecordingConnection();
        connection.Open();

        var error = Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var lockHandle = Dialect(name).AcquireMigrationLock(connection);
            connection.Close();   // what a server does to a session after a severe error
            throw new InvalidOperationException("the error that ended the session");
        }));

        Assert.Equal("the error that ended the session", error.Message);
        Assert.Single(connection.Executed);
    }

    /// <summary>A connection that records each statement and, like the real providers, refuses one while it is closed.</summary>
    private sealed class RecordingConnection : DbConnection
    {
        private ConnectionState _state = ConnectionState.Closed;

        public List<string> Executed { get; } = [];

        [AllowNull]
        public override string ConnectionString { get; set; } = "";
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "1";
        public override ConnectionState State => _state;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => _state = ConnectionState.Closed;
        public override void Open() => _state = ConnectionState.Open;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new RecordingCommand(this);

        public object Run(string sql)
        {
            if (_state != ConnectionState.Open)
                throw new InvalidOperationException($"ExecuteNonQuery requires an open and available Connection. The connection's current state is {_state.ToString().ToLowerInvariant()}.");
            Executed.Add(sql);
            return 0;
        }
    }

    private sealed class RecordingCommand(RecordingConnection connection) : DbCommand
    {
        [AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery() => (int)connection.Run(CommandText);
        public override object? ExecuteScalar() => connection.Run(CommandText);
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();
    }
}
