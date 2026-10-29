using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Sql;
using Grpc.Core;

namespace DbDataKit.Connections.FlightSql;

/// <summary>
/// A read-only command: a query in, a streaming reader out. <see cref="ExecuteNonQuery"/> is not
/// supported — this driver reads.
/// </summary>
public sealed class FlightSqlCommand : DbCommand
{
    private readonly FlightSqlParameterCollection _parameters = new();
    private FlightSqlConnection _connection;
    private CancellationTokenSource? _running;

    internal FlightSqlCommand(FlightSqlConnection connection)
    {
        _connection = connection;
        CommandTimeout = connection.Settings.CommandTimeout == Timeout.InfiniteTimeSpan
            ? 0
            : (int)connection.Settings.CommandTimeout.TotalSeconds;
    }

    [AllowNull]
    public override string CommandText { get; set; } = "";

    /// <summary>Seconds; 0 for no limit — ADO.NET's convention, and what
    /// <c>ConnectionTimeouts.CreateTimedCommand</c> stamps.</summary>
    public override int CommandTimeout { get; set; }

    public override CommandType CommandType
    {
        get => CommandType.Text;
        set
        {
            if (value != CommandType.Text)
                throw new NotSupportedException("Only text commands are supported.");
        }
    }

    protected override DbConnection? DbConnection
    {
        get => _connection;
        set => _connection = value as FlightSqlConnection
            ?? throw new ArgumentException($"A {nameof(FlightSqlCommand)} runs on a {nameof(FlightSqlConnection)}.");
    }

    protected override DbParameterCollection DbParameterCollection => _parameters;

    protected override DbTransaction? DbTransaction
    {
        get => null;
        set
        {
            if (value is not null)
                throw new NotSupportedException("The Arrow Flight SQL driver has no transactions.");
        }
    }

    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.None;

    public override void Cancel() => _running?.Cancel();

    public override void Prepare() { }

    public override int ExecuteNonQuery() => throw ReadOnly();

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken) => throw ReadOnly();

    public override object? ExecuteScalar() => ExecuteScalarAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        await using var reader = await ExecuteDbDataReaderAsync(CommandBehavior.Default, cancellationToken);
        return await reader.ReadAsync(cancellationToken) && reader.FieldCount > 0
            ? (reader.IsDBNull(0) ? null : reader.GetValue(0))
            : null;
    }

    protected override DbParameter CreateDbParameter() => new FlightSqlParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior, CancellationToken cancellationToken)
    {
        if (_connection.State != ConnectionState.Open)
            throw new InvalidOperationException("The connection is not open.");

        _running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _running.Token;

        var timeout = CommandTimeout <= 0 ? Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(CommandTimeout);
        var options = _connection.CallOptions(timeout);

        PreparedHandle? prepared = null;
        try
        {
            var (sql, ordered) = Statement(inline: false);

            FlightInfo info;
            if (ordered.Count == 0)
            {
                info = await _connection.Client.ExecuteAsync(sql, Apache.Arrow.Flight.Sql.Transaction.NoTransaction, options, token);
            }
            else
            {
                try
                {
                    prepared = await PreparedHandle.CreateAsync(_connection.Client, sql, options, token);
                    info = await prepared.BindAndExecuteAsync(ArrowParameters.Build(ordered), options, token);
                }
                catch (Exception ex) when (IsUnimplemented(ex))
                {
                    // A server that prepares statements but cannot bind values to them (some proxies):
                    // remember it and send the values as literals instead.
                    _connection.ServerRefusesParameters = true;
                    if (prepared is not null)
                        await prepared.CloseQuietlyAsync(options);
                    prepared = null;
                    info = await _connection.Client.ExecuteAsync(
                        Statement(inline: true).Sql, Apache.Arrow.Flight.Sql.Transaction.NoTransaction, options, token);
                }
            }

            var reader = new FlightSqlDataReader(_connection, info, options, prepared, behavior, token);
            prepared = null; // the reader owns it now
            await reader.InitializeAsync();
            return reader;
        }
        catch (Exception ex) when (FlightSqlException.TryWrap(ex, out var wrapped))
        {
            throw wrapped;
        }
        finally
        {
            if (prepared is not null)
                await prepared.CloseQuietlyAsync(options);
        }
    }

    /// <summary>
    /// The statement as it goes to the server, and — for a prepared statement — the parameter values in
    /// the order their markers appear. Inline mode renders each value as a literal through the
    /// connection's dialect and returns no ordered values.
    /// </summary>
    private (string Sql, List<object?> Ordered) Statement(bool inline)
    {
        var known = _parameters.Items.Select(p => p.ParameterName.TrimStart('@', ':', '$')).ToList();
        var ordered = new List<object?>();

        if (known.Count == 0)
            return (CommandText, ordered);

        FlightSqlParameter Find(string name) =>
            _parameters.Items.First(p => FlightSqlParameterCollection.Matches(p.ParameterName, name));

        if (inline || _connection.ServerRefusesParameters || _connection.Settings.ParameterMode == FlightSqlParameterMode.Inline)
            return (ParameterMarkers.Replace(CommandText, known, name => _connection.RenderLiteral(Find(name).Value)), ordered);

        var sql = ParameterMarkers.Replace(CommandText, known, name =>
        {
            ordered.Add(Find(name).Value);
            return "?";
        });
        return (sql, ordered);
    }

    private static bool IsUnimplemented(Exception ex) =>
        ex is RpcException { StatusCode: StatusCode.Unimplemented }
        || (ex is AggregateException agg && agg.InnerExceptions.Any(IsUnimplemented))
        || (ex.InnerException is { } inner && IsUnimplemented(inner));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _running?.Dispose();
        base.Dispose(disposing);
    }

    private static NotSupportedException ReadOnly() =>
        new("The Arrow Flight SQL driver is read-only; it can run queries but not statements that change data.");
}
