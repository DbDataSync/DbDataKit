using System.Collections;
using System.Data;
using System.Data.Common;

namespace DbDataKit.Metadata.Tests;

/// <summary>A command as the provider saw it: text, bound parameters (name, value) and behavior.</summary>
public sealed record FakeCommand(string Sql, IReadOnlyList<(string Name, object? Value)> Parameters, CommandBehavior Behavior);

/// <summary>A canned answer: rows, plus an optional provider schema table (what GetSchemaTable returns).</summary>
public sealed record FakeResult(DataTable Data, DataTable? Schema = null)
{
    public static FakeResult Rows(string[] columns, params object?[][] rows)
    {
        var table = new DataTable();
        for (int c = 0; c < columns.Length; c++)
        {
            // The column type comes from the first non-null value so GetFieldType is meaningful.
            Type type = rows.Select(r => r[c]).FirstOrDefault(v => v is not null)?.GetType() ?? typeof(string);
            table.Columns.Add(columns[c], type);
        }
        foreach (var row in rows) table.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());
        return new FakeResult(table);
    }
}

/// <summary>A DbConnection that records every command and answers from a handler, so SQL text and result mapping are tested without a server.</summary>
public sealed class FakeDbConnection(Func<FakeCommand, FakeResult?> handler, string serverVersion = "0.0") : DbConnection
{
    private ConnectionState state = ConnectionState.Closed;
    public List<FakeCommand> Log { get; } = [];
    public int Opens { get; private set; }

    internal FakeResult Answer(FakeCommand command)
    {
        Log.Add(command);
        return handler(command) ?? FakeResult.Rows(["x"]);
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString { get; set; } = "";
    public override string Database => "fake";
    public override string DataSource => "fake";
    public override string ServerVersion => serverVersion;
    public override ConnectionState State => state;
    public override void ChangeDatabase(string databaseName) { }
    public override void Close() => state = ConnectionState.Closed;
    public override void Open() { state = ConnectionState.Open; Opens++; }
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => new FakeDbCommand(this);
}

internal sealed class FakeDbCommand(FakeDbConnection owner) : DbCommand
{
    private readonly FakeParameters parameters = new();

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string CommandText { get; set; } = "";
    public override int CommandTimeout { get; set; }
    public override CommandType CommandType { get; set; }
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get => owner; set { } }
    protected override DbParameterCollection DbParameterCollection => parameters;
    protected override DbTransaction? DbTransaction { get; set; }
    public override void Cancel() { }
    public override int ExecuteNonQuery() => throw new NotSupportedException();
    public override object? ExecuteScalar() => throw new NotSupportedException();
    public override void Prepare() { }
    protected override DbParameter CreateDbParameter() => new FakeParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        var args = parameters.Items.Select(p => (p.ParameterName, p.Value is DBNull ? null : p.Value)).ToList();
        return new FakeReader(owner.Answer(new FakeCommand(CommandText, args, behavior)));
    }
}

internal sealed class FakeParameter : DbParameter
{
    public override DbType DbType { get; set; }
    public override ParameterDirection Direction { get; set; }
    public override bool IsNullable { get; set; }
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ParameterName { get; set; } = "";
    public override int Size { get; set; }
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string SourceColumn { get; set; } = "";
    public override bool SourceColumnNullMapping { get; set; }
    public override object? Value { get; set; }
    public override void ResetDbType() { }
}

internal sealed class FakeParameters : DbParameterCollection
{
    public List<DbParameter> Items { get; } = [];
    public override int Count => Items.Count;
    public override object SyncRoot => Items;
    public override int Add(object value) { Items.Add((DbParameter)value); return Items.Count - 1; }
    public override void AddRange(Array values) { foreach (var v in values) Add(v!); }
    public override void Clear() => Items.Clear();
    public override bool Contains(object value) => Items.Contains((DbParameter)value);
    public override bool Contains(string value) => Items.Any(p => p.ParameterName == value);
    public override void CopyTo(Array array, int index) => Items.ToArray().CopyTo(array, index);
    public override IEnumerator GetEnumerator() => Items.GetEnumerator();
    public override int IndexOf(object value) => Items.IndexOf((DbParameter)value);
    public override int IndexOf(string parameterName) => Items.FindIndex(p => p.ParameterName == parameterName);
    public override void Insert(int index, object value) => Items.Insert(index, (DbParameter)value);
    public override void Remove(object value) => Items.Remove((DbParameter)value);
    public override void RemoveAt(int index) => Items.RemoveAt(index);
    public override void RemoveAt(string parameterName) => Items.RemoveAt(IndexOf(parameterName));
    protected override DbParameter GetParameter(int index) => Items[index];
    protected override DbParameter GetParameter(string parameterName) => Items[IndexOf(parameterName)];
    protected override void SetParameter(int index, DbParameter value) => Items[index] = value;
    protected override void SetParameter(string parameterName, DbParameter value) => Items[IndexOf(parameterName)] = value;
}

internal sealed class FakeReader(FakeResult result) : DbDataReader
{
    private readonly DataTable table = result.Data;
    private int row = -1;

    public override int Depth => 0;
    public override int FieldCount => table.Columns.Count;
    public override bool HasRows => table.Rows.Count > 0;
    public override bool IsClosed => false;
    public override int RecordsAffected => -1;
    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));
    public override bool Read() => ++row < table.Rows.Count;
    public override bool NextResult() => false;
    public override string GetName(int ordinal) => table.Columns[ordinal].ColumnName;
    public override int GetOrdinal(string name) => table.Columns.IndexOf(name);
    public override Type GetFieldType(int ordinal) => table.Columns[ordinal].DataType;
    public override string GetDataTypeName(int ordinal) =>
        table.Columns[ordinal].ExtendedProperties["typeName"] as string ?? table.Columns[ordinal].DataType.Name;
    public override DataTable? GetSchemaTable() => result.Schema ?? throw new NotSupportedException();
    public override bool IsDBNull(int ordinal) => GetValue(ordinal) is DBNull;
    public override object GetValue(int ordinal) => table.Rows[row][ordinal];
    public override int GetValues(object[] values)
    {
        int n = Math.Min(values.Length, FieldCount);
        for (int i = 0; i < n; i++) values[i] = GetValue(i);
        return n;
    }
    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(GetValue(ordinal));
    public override byte GetByte(int ordinal) => Convert.ToByte(GetValue(ordinal));
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
    public override char GetChar(int ordinal) => Convert.ToChar(GetValue(ordinal));
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(GetValue(ordinal));
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(GetValue(ordinal));
    public override double GetDouble(int ordinal) => Convert.ToDouble(GetValue(ordinal));
    public override float GetFloat(int ordinal) => Convert.ToSingle(GetValue(ordinal));
    public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
    public override short GetInt16(int ordinal) => Convert.ToInt16(GetValue(ordinal));
    public override int GetInt32(int ordinal) => Convert.ToInt32(GetValue(ordinal));
    public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal));
    public override string GetString(int ordinal) => Convert.ToString(GetValue(ordinal))!;
    public override IEnumerator GetEnumerator() => table.Rows.GetEnumerator();
}
