#pragma warning disable CS8765, CS8764
using System.Data;
using System.Data.Common;

namespace DbDataKit.Quality.Evaluation.Tests;

/// <summary>An engine that answers no statement: it records what was sent and then hangs until it is cancelled or throws what the test says.</summary>
public sealed class FakeConnection(Func<string, CancellationToken, Task<DbDataReader>> answer) : DbConnection
{
    public List<string> Sent { get; } = [];

    internal Func<string, CancellationToken, Task<DbDataReader>> Answer { get; } = answer;

    public static FakeConnection Hanging() => new(async (_, token) => { await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException("unreachable"); });

    public static FakeConnection Throwing(Exception exception) => new((_, _) => Task.FromException<DbDataReader>(exception));

    public override string ConnectionString { get; set; } = "";
    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => "0";
    public override ConnectionState State => ConnectionState.Open;
    public override void ChangeDatabase(string databaseName) { }
    public override void Close() { }
    public override void Open() { }
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => new FakeCommand(this);
}

internal sealed class FakeCommand(FakeConnection owner) : DbCommand
{
    public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; } = owner;
    protected override DbParameterCollection DbParameterCollection => throw new NotSupportedException();
    protected override DbTransaction? DbTransaction { get; set; }
    public override void Cancel() { }
    public override int ExecuteNonQuery() => throw new NotSupportedException();
    public override object? ExecuteScalar() => throw new NotSupportedException();
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => throw new NotSupportedException();
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => throw new NotSupportedException();

    protected override Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        owner.Sent.Add(CommandText);
        return owner.Answer(CommandText, cancellationToken);
    }
}

/// <summary>An exception of a driver: a SQLSTATE, a number as SQL Server has one, and a message that may hold a data value.</summary>
public sealed class FakeDbException(string message, string? sqlState = null, int number = 0) : DbException(message)
{
    public override string? SqlState { get; } = sqlState;

    public int Number { get; } = number;
}

/// <summary>A clock the test sets.</summary>
public sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
