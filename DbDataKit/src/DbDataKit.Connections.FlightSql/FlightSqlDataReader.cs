using System.Collections;
using System.Data.Common;
using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Sql;
using Grpc.Core;

namespace DbDataKit.Connections.FlightSql;

/// <summary>
/// Streams a query's result: each of the <see cref="FlightInfo"/>'s endpoints is fetched in order with
/// <c>DoGet</c>, and rows are handed out one at a time from the current Arrow record batch. Nothing is
/// buffered beyond the batch being read, so a result larger than memory is fine.
/// <para>
/// The first batch is fetched when the reader is created, not on the first <see cref="Read"/> —
/// callers ask for the column names before reading a row, and a server is free to leave the schema out
/// of the <see cref="FlightInfo"/> and only state it with the data.
/// </para>
/// <para>
/// Endpoints are read from the server the connection is already talking to, in order. A location a
/// server lists on an endpoint that points somewhere else is ignored — see phase 197F.
/// </para>
/// </summary>
public sealed class FlightSqlDataReader : DbDataReader
{
    private readonly FlightSqlConnection _connection;
    private readonly FlightInfo _info;
    private readonly FlightCallOptions _options;
    private readonly CancellationToken _token;
    private PreparedStatement? _prepared;

    private IAsyncEnumerator<RecordBatch>? _batches;
    private int _endpoint;
    private RecordBatch? _current;
    private int _row = -1;
    private Schema _schema;
    private bool _closed;
    private bool _hasRows;

    internal FlightSqlDataReader(
        FlightSqlConnection connection, FlightInfo info, FlightCallOptions options,
        PreparedStatement? prepared, System.Data.CommandBehavior behavior, CancellationToken token)
    {
        _connection = connection;
        _info = info;
        _options = options;
        _prepared = prepared;
        _token = token;
        _schema = info.Schema ?? new Schema([], null);
    }

    internal async Task InitializeAsync()
    {
        _hasRows = await AdvanceBatchAsync();
        if (_schema.FieldsList.Count == 0 && _current is not null)
            _schema = _current.Schema;
    }

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        if (_closed)
            return false;

        try
        {
            if (_current is not null && _row + 1 < _current.Length)
            {
                _row++;
                return true;
            }

            if (!await AdvanceBatchAsync())
                return false;

            _row = 0;
            return true;
        }
        catch (Exception ex) when (FlightSqlException.TryWrap(ex, out var wrapped))
        {
            throw wrapped;
        }
    }

    public override bool Read() => ReadAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Moves to the next non-empty record batch, across endpoints. False when the result is
    /// exhausted.</summary>
    private async Task<bool> AdvanceBatchAsync()
    {
        while (true)
        {
            if (_batches is null)
            {
                if (_endpoint >= _info.Endpoints.Count)
                {
                    _current = null;
                    return false;
                }

                var ticket = _info.Endpoints[_endpoint++].Ticket;
                _batches = _connection.DoGetAsync(ticket, _options, _token).GetAsyncEnumerator(_token);
            }

            if (!await _batches.MoveNextAsync())
            {
                await _batches.DisposeAsync();
                _batches = null;
                continue;
            }

            _current = _batches.Current;
            _row = -1;
            if (_current.Length > 0)
                return true;
        }
    }

    public override bool HasRows => _hasRows;
    public override bool IsClosed => _closed;
    public override int Depth => 0;
    public override int RecordsAffected => -1;
    public override int FieldCount => _schema.FieldsList.Count;

    public override bool NextResult() => false;

    public override string GetName(int ordinal) => _schema.FieldsList[ordinal].Name;

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < _schema.FieldsList.Count; i++)
        {
            if (string.Equals(_schema.FieldsList[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        throw new IndexOutOfRangeException($"No column named '{name}'.");
    }

    public override string GetDataTypeName(int ordinal) => ArrowValues.NativeTypeName(_schema.FieldsList[ordinal].DataType);
    public override Type GetFieldType(int ordinal) => ArrowValues.ClrType(_schema.FieldsList[ordinal].DataType);

    public override bool IsDBNull(int ordinal) => Column(ordinal).IsNull(_row);

    public override object GetValue(int ordinal) => ArrowValues.ValueAt(Column(ordinal), _row) ?? DBNull.Value;

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
            values[i] = GetValue(i);
        return count;
    }

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool GetBoolean(int ordinal) => As<bool>(ordinal);
    public override byte GetByte(int ordinal) => As<byte>(ordinal);
    public override char GetChar(int ordinal) => As<string>(ordinal) is { Length: > 0 } s ? s[0] : throw new InvalidCastException();
    public override DateTime GetDateTime(int ordinal) => As<DateTime>(ordinal);
    public override decimal GetDecimal(int ordinal) => As<decimal>(ordinal);
    public override double GetDouble(int ordinal) => As<double>(ordinal);
    public override float GetFloat(int ordinal) => As<float>(ordinal);
    public override short GetInt16(int ordinal) => As<short>(ordinal);
    public override int GetInt32(int ordinal) => As<int>(ordinal);
    public override long GetInt64(int ordinal) => As<long>(ordinal);
    public override string GetString(int ordinal) => As<string>(ordinal);

    public override Guid GetGuid(int ordinal) => GetValue(ordinal) switch
    {
        Guid g => g,
        string s => Guid.Parse(s),
        byte[] { Length: 16 } b => new Guid(b),
        var other => throw new InvalidCastException($"A {other.GetType().Name} is not a Guid."),
    };

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var bytes = As<byte[]>(ordinal);
        if (buffer is null)
            return bytes.Length;

        var count = (int)Math.Min(length, bytes.Length - dataOffset);
        System.Array.Copy(bytes, dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var text = As<string>(ordinal);
        if (buffer is null)
            return text.Length;

        var count = (int)Math.Min(length, text.Length - dataOffset);
        text.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);

    private IArrowArray Column(int ordinal)
    {
        if (_current is null || _row < 0 || _row >= _current.Length)
            throw new InvalidOperationException("No current row. Call Read first.");

        return _current.Column(ordinal);
    }

    /// <summary>The value as <typeparamref name="T"/>, converting where the types are compatible — a
    /// server's idea of a column's width need not match the caller's (an <c>int</c> column asked for as a
    /// <c>long</c>).</summary>
    private T As<T>(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is T typed)
            return typed;

        if (value is DBNull)
            throw new InvalidCastException($"Column '{GetName(ordinal)}' is null.");

        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    public override void Close()
    {
        if (_closed)
            return;

        _closed = true;
        _current = null;
        CloseAsync().GetAwaiter().GetResult();
    }

    public override async Task CloseAsync()
    {
        _closed = true;
        _current = null;

        if (_batches is not null)
        {
            try { await _batches.DisposeAsync(); }
            catch (Exception) { /* abandoning a stream the server may already have ended */ }
            _batches = null;
        }

        if (_prepared is not null)
        {
            try { await _prepared.CloseAsync(_options, CancellationToken.None); }
            catch (Exception) { /* released with the connection otherwise */ }
            _prepared = null;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await CloseAsync();
        await base.DisposeAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Close();
        base.Dispose(disposing);
    }
}
