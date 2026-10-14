using System.Runtime.InteropServices;
using System.Text;

namespace DbData.Odbc.Metadata;

/// <summary>
/// The real driver manager's catalog functions, called directly: System.Data.Odbc offers GetSchema for tables, columns and
/// indexes and nothing for primary or foreign keys. It opens a connection of its own from the connection string (the
/// provider keeps its handle private) and calls only catalog functions and SQLGetInfo. SQLWCHAR is read and written as
/// UTF-16, which is what the Windows driver manager and unixODBC use. The bitness of the process decides which drivers load.
/// </summary>
internal sealed class OdbcNativeFunctions(string connectionString) : IOdbcCatalogFunctions
{
    private const string Library = "dbdata-odbc-driver-manager";
    private const short HandleEnv = 1, HandleDbc = 2, HandleStmt = 3, NullTerminated = -3;

    private nint environment, connection;
    private readonly SemaphoreSlim one = new(1, 1);

    static OdbcNativeFunctions()
    {
        // The driver manager's library has a different name on each system; System.Data.Odbc uses the same names.
        try { NativeLibrary.SetDllImportResolver(typeof(OdbcNativeFunctions).Assembly, (name, _, _) => name == Library ? LoadDriverManager() : 0); }
        catch (InvalidOperationException) { /* a resolver is already set for this assembly */ }
    }

    private static nint LoadDriverManager()
    {
        string[] names = OperatingSystem.IsWindows() ? ["odbc32.dll"]
            : OperatingSystem.IsMacOS() ? ["libodbc.2.dylib", "libodbc.dylib"]
            : ["libodbc.so.2", "libodbc.so.1", "libodbc.so"];
        foreach (string name in names)
            if (NativeLibrary.TryLoad(name, out nint handle)) return handle;
        throw new DllNotFoundException("The ODBC driver manager could not be loaded (" + string.Join(", ", names) + ").");
    }

    [DllImport(Library)] private static extern short SQLAllocHandle(short handleType, nint input, out nint output);
    [DllImport(Library)] private static extern short SQLFreeHandle(short handleType, nint handle);
    [DllImport(Library)] private static extern short SQLSetEnvAttr(nint env, int attribute, nint value, int length);
    [DllImport(Library)] private static extern short SQLSetConnectAttr(nint dbc, int attribute, nint value, int length);
    [DllImport(Library)] private static extern short SQLDriverConnectW(nint dbc, nint window, [MarshalAs(UnmanagedType.LPWStr)] string input, short inputLength, nint output, short outputMax, out short outputLength, ushort completion);
    [DllImport(Library)] private static extern short SQLDisconnect(nint dbc);
    [DllImport(Library)] private static extern short SQLGetInfoW(nint dbc, ushort infoType, byte[] value, short bufferLength, out short stringLength);
    [DllImport(Library)] private static extern short SQLTablesW(nint stmt,
        [MarshalAs(UnmanagedType.LPWStr)] string? catalog, short catalogLength, [MarshalAs(UnmanagedType.LPWStr)] string? schema, short schemaLength,
        [MarshalAs(UnmanagedType.LPWStr)] string? table, short tableLength, [MarshalAs(UnmanagedType.LPWStr)] string? types, short typesLength);
    [DllImport(Library)] private static extern short SQLColumnsW(nint stmt,
        [MarshalAs(UnmanagedType.LPWStr)] string? catalog, short catalogLength, [MarshalAs(UnmanagedType.LPWStr)] string? schema, short schemaLength,
        [MarshalAs(UnmanagedType.LPWStr)] string? table, short tableLength, [MarshalAs(UnmanagedType.LPWStr)] string? column, short columnLength);
    [DllImport(Library)] private static extern short SQLPrimaryKeysW(nint stmt,
        [MarshalAs(UnmanagedType.LPWStr)] string? catalog, short catalogLength, [MarshalAs(UnmanagedType.LPWStr)] string? schema, short schemaLength,
        [MarshalAs(UnmanagedType.LPWStr)] string? table, short tableLength);
    [DllImport(Library)] private static extern short SQLForeignKeysW(nint stmt,
        [MarshalAs(UnmanagedType.LPWStr)] string? pkCatalog, short pkCatalogLength, [MarshalAs(UnmanagedType.LPWStr)] string? pkSchema, short pkSchemaLength,
        [MarshalAs(UnmanagedType.LPWStr)] string? pkTable, short pkTableLength,
        [MarshalAs(UnmanagedType.LPWStr)] string? fkCatalog, short fkCatalogLength, [MarshalAs(UnmanagedType.LPWStr)] string? fkSchema, short fkSchemaLength,
        [MarshalAs(UnmanagedType.LPWStr)] string? fkTable, short fkTableLength);
    [DllImport(Library)] private static extern short SQLStatisticsW(nint stmt,
        [MarshalAs(UnmanagedType.LPWStr)] string? catalog, short catalogLength, [MarshalAs(UnmanagedType.LPWStr)] string? schema, short schemaLength,
        [MarshalAs(UnmanagedType.LPWStr)] string? table, short tableLength, ushort unique, ushort reserved);
    [DllImport(Library)] private static extern short SQLNumResultCols(nint stmt, out short columns);
    [DllImport(Library)] private static extern short SQLFetch(nint stmt);
    [DllImport(Library)] private static extern short SQLGetData(nint stmt, ushort column, short targetType, byte[] buffer, nint bufferLength, out nint indicator);
    [DllImport(Library)] private static extern short SQLGetDiagRecW(short handleType, nint handle, short record,
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder state, out int nativeError,
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder message, short messageMax, out short messageLength);

    private static bool Ok(short ret) => ret is 0 or 1;   // SQL_SUCCESS, SQL_SUCCESS_WITH_INFO

    private static string State(short handleType, nint handle)
    {
        var state = new StringBuilder(8);
        try { return Ok(SQLGetDiagRecW(handleType, handle, 1, state, out _, new StringBuilder(16), 16, out _)) ? state.ToString() : "HY000"; }
        catch (Exception) { return "HY000"; }
    }

    private void Connect()
    {
        if (connection != 0) return;
        if (!Ok(SQLAllocHandle(HandleEnv, 0, out nint env))) throw new OdbcCatalogException("SQLAllocHandle", "HY001");
        environment = env;
        SQLSetEnvAttr(env, 200, 3, 0);                                   // SQL_ATTR_ODBC_VERSION = SQL_OV_ODBC3
        if (!Ok(SQLAllocHandle(HandleDbc, env, out nint dbc))) throw new OdbcCatalogException("SQLAllocHandle", State(HandleEnv, env));
        SQLSetConnectAttr(dbc, 103, 30, 0);                              // SQL_ATTR_LOGIN_TIMEOUT
        short ret = SQLDriverConnectW(dbc, 0, OdbcConnectionString.Normalize(connectionString), NullTerminated, 0, 0, out _, 0); // SQL_DRIVER_NOPROMPT
        if (!Ok(ret))
        {
            string state = State(HandleDbc, dbc);
            SQLFreeHandle(HandleDbc, dbc);
            throw new OdbcCatalogException("SQLDriverConnect", state);
        }
        connection = dbc;
    }

    private static short Len(string? s) => s is null ? (short)0 : NullTerminated;

    private async Task<T> SerializedAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        await one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(work, cancellationToken).ConfigureAwait(false); }
        finally { one.Release(); }
    }

    // One catalog function: a statement handle for it, every row of its result as text, the handle freed.
    private Task<IReadOnlyList<string?[]>> RunAsync(string function, Func<nint, short> call, CancellationToken cancellationToken) =>
        SerializedAsync<IReadOnlyList<string?[]>>(() => Fetch(function, call, cancellationToken), cancellationToken);

    private List<string?[]> Fetch(string function, Func<nint, short> call, CancellationToken cancellationToken)
    {
        Connect();
        if (!Ok(SQLAllocHandle(HandleStmt, connection, out nint stmt))) throw new OdbcCatalogException(function, State(HandleDbc, connection));
        try
        {
            if (!Ok(call(stmt))) throw new OdbcCatalogException(function, State(HandleStmt, stmt));
            SQLNumResultCols(stmt, out short width);
            var rows = new List<string?[]>();
            var buffer = new byte[4096];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                short ret = SQLFetch(stmt);
                if (ret == 100) break;                                    // SQL_NO_DATA
                if (!Ok(ret)) throw new OdbcCatalogException(function, State(HandleStmt, stmt));
                var row = new string?[width];
                for (ushort c = 1; c <= width; c++) row[c - 1] = Text(stmt, c, buffer, function);
                rows.Add(row);
            }
            return rows;
        }
        finally { SQLFreeHandle(HandleStmt, stmt); }
    }

    private static string? Text(nint stmt, ushort column, byte[] buffer, string function)
    {
        var text = new StringBuilder();
        while (true)
        {
            short ret = SQLGetData(stmt, column, -8, buffer, buffer.Length, out nint indicator);   // SQL_C_WCHAR
            if (ret == 100) break;
            if (!Ok(ret)) throw new OdbcCatalogException(function, State(HandleStmt, stmt));
            if (indicator == -1) return text.Length == 0 ? null : text.ToString();                   // SQL_NULL_DATA
            // Truncated (SUCCESS_WITH_INFO): a full buffer less the terminator; otherwise exactly what the driver reports.
            int available = ret == 1 && (indicator == -4 || indicator >= buffer.Length) ? buffer.Length - 2 : (int)Math.Min(indicator, buffer.Length - 2);
            text.Append(Encoding.Unicode.GetString(buffer, 0, Math.Max(0, available - available % 2)));
            if (ret == 0) break;
        }
        return text.ToString();
    }

    public Task<IReadOnlyList<string?[]>> TablesAsync(string? catalog, string? schema, string? table, string? types, CancellationToken cancellationToken) =>
        RunAsync("SQLTables", s => SQLTablesW(s, catalog, Len(catalog), schema, Len(schema), table, Len(table), types, Len(types)), cancellationToken);

    public Task<IReadOnlyList<string?[]>> ColumnsAsync(string? catalog, string? schema, string? table, string? column, CancellationToken cancellationToken) =>
        RunAsync("SQLColumns", s => SQLColumnsW(s, catalog, Len(catalog), schema, Len(schema), table, Len(table), column, Len(column)), cancellationToken);

    public Task<IReadOnlyList<string?[]>> PrimaryKeysAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken) =>
        RunAsync("SQLPrimaryKeys", s => SQLPrimaryKeysW(s, catalog, Len(catalog), schema, Len(schema), table, NullTerminated), cancellationToken);

    public Task<IReadOnlyList<string?[]>> ForeignKeysAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken) =>
        RunAsync("SQLForeignKeys", s => SQLForeignKeysW(s, null, 0, null, 0, null, 0, catalog, Len(catalog), schema, Len(schema), table, NullTerminated), cancellationToken);

    public Task<IReadOnlyList<string?[]>> StatisticsAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken) =>
        RunAsync("SQLStatistics", s => SQLStatisticsW(s, catalog, Len(catalog), schema, Len(schema), table, NullTerminated, 1, 0), cancellationToken); // SQL_INDEX_ALL, SQL_QUICK

    public Task<string?> GetInfoStringAsync(ushort infoType, CancellationToken cancellationToken) =>
        SerializedAsync<string?>(() =>
        {
            Connect();
            var buffer = new byte[1024];
            if (!Ok(SQLGetInfoW(connection, infoType, buffer, (short)buffer.Length, out short length))) throw new OdbcCatalogException("SQLGetInfo", State(HandleDbc, connection));
            int bytes = Math.Min(length, buffer.Length - 2);
            return Encoding.Unicode.GetString(buffer, 0, Math.Max(0, bytes - bytes % 2));
        }, cancellationToken);

    public Task<uint> GetInfoNumberAsync(ushort infoType, CancellationToken cancellationToken) =>
        SerializedAsync(() =>
        {
            Connect();
            var buffer = new byte[4];   // zeroed: a SQLUSMALLINT answer fills only the low two bytes
            if (!Ok(SQLGetInfoW(connection, infoType, buffer, (short)buffer.Length, out _))) throw new OdbcCatalogException("SQLGetInfo", State(HandleDbc, connection));
            return BitConverter.ToUInt32(buffer, 0);
        }, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (connection != 0) { SQLDisconnect(connection); SQLFreeHandle(HandleDbc, connection); connection = 0; }
        if (environment != 0) { SQLFreeHandle(HandleEnv, environment); environment = 0; }
        return ValueTask.CompletedTask;
    }
}
