using System.Text;

namespace DbDataKit.Odbc.Metadata.Tests;

/// <summary>
/// A scripted driver manager: every catalog call "succeeds" and returns <see cref="Rows"/>; SQLGetInfo answers from <see cref="Info"/>.
/// It records what the native layer did (attributes set, handles freed, connects) and can block a fetch until SQLCancel arrives.
/// </summary>
internal sealed class FakeOdbcApi : IOdbcApi
{
    public string?[][] Rows = [];
    public int ColumnCount = -1;
    public Dictionary<ushort, string> Info = [];
    public bool InfoAsUtf32;
    public short DriverConnectResult;
    public string State = "HY000";
    public short CatalogResult;

    public int Connects, Disconnects, Cancels, StatementsOpened, FetchCalls;
    public List<string> ConnectionStrings = [];
    public List<(int Attribute, nint Value)> ConnectionAttributes = [], StatementAttributes = [];
    public List<int> InfoBufferSizes = [];
    public HashSet<nint> OpenStatements = [];
    public string? LastFunction;
    public (string? Catalog, string? Schema, string? Name)? LastArgs;

    public ManualResetEventSlim? FetchEntered;      // set when a fetch blocks
    public bool BlockFetchUntilCancelled;
    private readonly ManualResetEventSlim cancelled = new();

    private nint next = 1000;
    private readonly Dictionary<nint, int> cursor = [];
    private readonly Dictionary<(nint, ushort), int> offsets = [];

    public short AllocHandle(short handleType, nint input, out nint output)
    {
        output = next++;
        if (handleType == 3) { OpenStatements.Add(output); cursor[output] = 0; StatementsOpened++; }
        return 0;
    }

    public short FreeHandle(short handleType, nint handle)
    {
        if (handleType == 3) OpenStatements.Remove(handle);
        return 0;
    }

    public short SetEnvAttr(nint env, int attribute, nint value) => 0;
    public short SetConnectAttr(nint dbc, int attribute, nint value) { ConnectionAttributes.Add((attribute, value)); return 0; }
    public short SetStmtAttr(nint stmt, int attribute, nint value) { StatementAttributes.Add((attribute, value)); return 0; }

    public short DriverConnect(nint dbc, string connectionString)
    {
        Connects++;
        ConnectionStrings.Add(connectionString);
        return DriverConnectResult;
    }

    public short Disconnect(nint dbc) { Disconnects++; return 0; }

    public short GetInfo(nint dbc, ushort infoType, Span<byte> value, out short length)
    {
        InfoBufferSizes.Add(value.Length);
        length = 0;
        if (!Info.TryGetValue(infoType, out string? text)) return -1;
        byte[] bytes = InfoAsUtf32 ? Encoding.UTF32.GetBytes(text) : Encoding.Unicode.GetBytes(text);
        length = (short)bytes.Length;
        int copy = Math.Max(0, Math.Min(bytes.Length, value.Length - 2));
        bytes.AsSpan(0, copy).CopyTo(value);
        if (value.Length >= copy + 2) { value[copy] = 0; value[copy + 1] = 0; }
        return (short)(bytes.Length > value.Length - 2 ? 1 : 0);
    }

    private short Catalog(nint stmt, string function, string? c, string? s, string? n)
    {
        LastFunction = function;
        LastArgs = (c, s, n);
        return CatalogResult;
    }

    public short Tables(nint stmt, string? catalog, string? schema, string? table, string? types) => Catalog(stmt, "SQLTables", catalog, schema, table);
    public short Columns(nint stmt, string? catalog, string? schema, string? table, string? column) => Catalog(stmt, "SQLColumns", catalog, schema, table);
    public short PrimaryKeys(nint stmt, string? catalog, string? schema, string table) => Catalog(stmt, "SQLPrimaryKeys", catalog, schema, table);
    public short ForeignKeys(nint stmt, string? catalog, string? schema, string table) => Catalog(stmt, "SQLForeignKeys", catalog, schema, table);
    public short Statistics(nint stmt, string? catalog, string? schema, string table) => Catalog(stmt, "SQLStatistics", catalog, schema, table);
    public short TypeInfo(nint stmt, short sqlType) => Catalog(stmt, "SQLGetTypeInfo", null, null, sqlType.ToString());
    public short Procedures(nint stmt, string? catalog, string? schema, string? procedure) => Catalog(stmt, "SQLProcedures", catalog, schema, procedure);
    public short ProcedureColumns(nint stmt, string? catalog, string? schema, string? procedure, string? column) => Catalog(stmt, "SQLProcedureColumns", catalog, schema, procedure);

    public short NumResultCols(nint stmt, out short columns)
    {
        columns = (short)(DescribedColumns.Length > 0 ? DescribedColumns.Length : ColumnCount >= 0 ? ColumnCount : Rows.Length > 0 ? Rows[0].Length : 0);
        return 0;
    }

    public List<string> Prepared = [];
    public (string Name, short SqlType, long Size, short Scale, short Nullable)[] DescribedColumns = [];
    public short PrepareResult;

    public short Prepare(nint stmt, string sql)
    {
        Prepared.Add(sql);
        LastFunction = "SQLPrepare";
        return PrepareResult;
    }

    public short DescribeCol(nint stmt, ushort column, out string name, out short sqlType, out long size, out short scale, out short nullable)
    {
        var c = DescribedColumns[column - 1];
        (name, sqlType, size, scale, nullable) = c;
        return 0;
    }

    public short Fetch(nint stmt)
    {
        Interlocked.Increment(ref FetchCalls);
        if (BlockFetchUntilCancelled)
        {
            FetchEntered?.Set();
            cancelled.Wait(TimeSpan.FromSeconds(10));
            State = "HY008";
            return -1;
        }
        int i = cursor[stmt]++;
        return (short)(i < Rows.Length ? 0 : 100);
    }

    public short GetData(nint stmt, ushort column, Span<byte> buffer, out nint indicator)
    {
        string? text = Rows[cursor[stmt] - 1][column - 1];
        if (text is null) { indicator = -1; return 0; }
        byte[] bytes = Encoding.Unicode.GetBytes(text);
        offsets.TryGetValue((stmt, column), out int offset);
        int remaining = bytes.Length - offset;
        int room = (buffer.Length - 2) & ~1;
        indicator = remaining;
        if (remaining <= room)
        {
            bytes.AsSpan(offset, remaining).CopyTo(buffer);
            offsets.Remove((stmt, column));
            return 0;
        }
        bytes.AsSpan(offset, room).CopyTo(buffer);
        offsets[(stmt, column)] = offset + room;
        return 1;
    }

    public short Cancel(nint stmt)
    {
        Interlocked.Increment(ref Cancels);
        cancelled.Set();
        return 0;
    }

    public string DiagState(short handleType, nint handle) => State;
}
