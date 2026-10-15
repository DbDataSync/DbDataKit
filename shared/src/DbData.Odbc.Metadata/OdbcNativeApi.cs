using System.Runtime.InteropServices;

namespace DbData.Odbc.Metadata;

/// <summary>
/// The ODBC entry points <see cref="OdbcNativeFunctions"/> needs, one method each, returning the raw SQLRETURN. Exists so the
/// fetch loop, timeouts, cancellation, buffer growth and policy can be tested against a scripted fake without a driver.
/// </summary>
internal interface IOdbcApi
{
    short AllocHandle(short handleType, nint input, out nint output);
    short FreeHandle(short handleType, nint handle);
    short SetEnvAttr(nint env, int attribute, nint value);
    short SetConnectAttr(nint dbc, int attribute, nint value);
    short SetStmtAttr(nint stmt, int attribute, nint value);
    short DriverConnect(nint dbc, string connectionString);
    short Disconnect(nint dbc);
    short GetInfo(nint dbc, ushort infoType, Span<byte> value, out short length);
    short Tables(nint stmt, string? catalog, string? schema, string? table, string? types);
    short Columns(nint stmt, string? catalog, string? schema, string? table, string? column);
    short PrimaryKeys(nint stmt, string? catalog, string? schema, string table);
    short ForeignKeys(nint stmt, string? catalog, string? schema, string table);
    short Statistics(nint stmt, string? catalog, string? schema, string table);
    short TypeInfo(nint stmt, short sqlType);
    short Procedures(nint stmt, string? catalog, string? schema, string? procedure);
    short ProcedureColumns(nint stmt, string? catalog, string? schema, string? procedure, string? column);
    short NumResultCols(nint stmt, out short columns);
    short Fetch(nint stmt);
    short GetData(nint stmt, ushort column, Span<byte> buffer, out nint indicator);
    short Cancel(nint stmt);

    /// <summary>The SQLSTATE of the first diagnostic record, or HY000 when there is none or it cannot be read.</summary>
    string DiagState(short handleType, nint handle);
}

/// <summary>
/// The real driver manager, called through <c>LibraryImport</c> (source generated, trim and AOT friendly). SQLWCHAR is read and
/// written as UTF-16 (the Windows driver manager and the default unixODBC build); <see cref="OdbcNativeFunctions"/> checks that
/// right after connecting. The bitness of the process decides which drivers load.
/// </summary>
/// <remarks>
/// Verified on Windows against odbc32.dll (allocation, connect failure and diagnostics). The catalog entry points are the same
/// pattern but are exercised against a real driver only by the opt-in SQL Server tests.
/// </remarks>
internal sealed partial class OdbcNativeApi : IOdbcApi
{
    public static readonly OdbcNativeApi Instance = new();

    private const string Library = "dbdata-odbc-driver-manager";
    private const short NullTerminated = -3;

    static OdbcNativeApi()
    {
        // The driver manager's library has a different name on each system; System.Data.Odbc uses the same names.
        try { NativeLibrary.SetDllImportResolver(typeof(OdbcNativeApi).Assembly, (name, _, _) => name == Library ? LoadDriverManager() : 0); }
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

    [LibraryImport(Library)] private static partial short SQLAllocHandle(short handleType, nint input, out nint output);
    [LibraryImport(Library)] private static partial short SQLFreeHandle(short handleType, nint handle);
    [LibraryImport(Library)] private static partial short SQLSetEnvAttr(nint env, int attribute, nint value, int length);
    [LibraryImport(Library)] private static partial short SQLSetConnectAttr(nint dbc, int attribute, nint value, int length);
    [LibraryImport(Library)] private static partial short SQLSetStmtAttr(nint stmt, int attribute, nint value, int length);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    private static partial short SQLDriverConnectW(nint dbc, nint window, string input, short inputLength, nint output, short outputMax, out short outputLength, ushort completion);
    [LibraryImport(Library)] private static partial short SQLDisconnect(nint dbc);
    [LibraryImport(Library)] private static partial short SQLGetInfoW(nint dbc, ushort infoType, Span<byte> value, short bufferLength, out short stringLength);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    private static partial short SQLTablesW(nint stmt, string? catalog, short catalogLength, string? schema, short schemaLength,
        string? table, short tableLength, string? types, short typesLength);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    private static partial short SQLColumnsW(nint stmt, string? catalog, short catalogLength, string? schema, short schemaLength,
        string? table, short tableLength, string? column, short columnLength);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    private static partial short SQLPrimaryKeysW(nint stmt, string? catalog, short catalogLength, string? schema, short schemaLength,
        string? table, short tableLength);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    private static partial short SQLForeignKeysW(nint stmt, string? pkCatalog, short pkCatalogLength, string? pkSchema, short pkSchemaLength,
        string? pkTable, short pkTableLength, string? fkCatalog, short fkCatalogLength, string? fkSchema, short fkSchemaLength,
        string? fkTable, short fkTableLength);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    private static partial short SQLStatisticsW(nint stmt, string? catalog, short catalogLength, string? schema, short schemaLength,
        string? table, short tableLength, ushort unique, ushort reserved);
    [LibraryImport(Library)] private static partial short SQLGetTypeInfoW(nint stmt, short dataType);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    private static partial short SQLProceduresW(nint stmt, string? catalog, short catalogLength, string? schema, short schemaLength,
        string? procedure, short procedureLength);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    private static partial short SQLProcedureColumnsW(nint stmt, string? catalog, short catalogLength, string? schema, short schemaLength,
        string? procedure, short procedureLength, string? column, short columnLength);
    [LibraryImport(Library)] private static partial short SQLNumResultCols(nint stmt, out short columns);
    [LibraryImport(Library)] private static partial short SQLFetch(nint stmt);
    [LibraryImport(Library)] private static partial short SQLGetData(nint stmt, ushort column, short targetType, Span<byte> buffer, nint bufferLength, out nint indicator);
    [LibraryImport(Library)] private static partial short SQLCancel(nint stmt);
    [LibraryImport(Library)]
    private static partial short SQLGetDiagRecW(short handleType, nint handle, short record, Span<byte> state, out int nativeError,
        Span<byte> message, short messageMax, out short messageLength);

    private static short Len(string? s) => s is null ? (short)0 : NullTerminated;

    public short AllocHandle(short handleType, nint input, out nint output) => SQLAllocHandle(handleType, input, out output);
    public short FreeHandle(short handleType, nint handle) => SQLFreeHandle(handleType, handle);
    public short SetEnvAttr(nint env, int attribute, nint value) => SQLSetEnvAttr(env, attribute, value, 0);
    public short SetConnectAttr(nint dbc, int attribute, nint value) => SQLSetConnectAttr(dbc, attribute, value, 0);
    public short SetStmtAttr(nint stmt, int attribute, nint value) => SQLSetStmtAttr(stmt, attribute, value, 0);
    public short DriverConnect(nint dbc, string connectionString) => SQLDriverConnectW(dbc, 0, connectionString, NullTerminated, 0, 0, out _, 0); // SQL_DRIVER_NOPROMPT
    public short Disconnect(nint dbc) => SQLDisconnect(dbc);
    public short GetInfo(nint dbc, ushort infoType, Span<byte> value, out short length) => SQLGetInfoW(dbc, infoType, value, (short)value.Length, out length);
    public short Tables(nint stmt, string? catalog, string? schema, string? table, string? types) =>
        SQLTablesW(stmt, catalog, Len(catalog), schema, Len(schema), table, Len(table), types, Len(types));
    public short Columns(nint stmt, string? catalog, string? schema, string? table, string? column) =>
        SQLColumnsW(stmt, catalog, Len(catalog), schema, Len(schema), table, Len(table), column, Len(column));
    public short PrimaryKeys(nint stmt, string? catalog, string? schema, string table) =>
        SQLPrimaryKeysW(stmt, catalog, Len(catalog), schema, Len(schema), table, NullTerminated);
    public short ForeignKeys(nint stmt, string? catalog, string? schema, string table) =>
        SQLForeignKeysW(stmt, null, 0, null, 0, null, 0, catalog, Len(catalog), schema, Len(schema), table, NullTerminated);
    public short Statistics(nint stmt, string? catalog, string? schema, string table) =>
        SQLStatisticsW(stmt, catalog, Len(catalog), schema, Len(schema), table, NullTerminated, 1, 0);   // SQL_INDEX_ALL, SQL_QUICK
    public short TypeInfo(nint stmt, short sqlType) => SQLGetTypeInfoW(stmt, sqlType);
    public short Procedures(nint stmt, string? catalog, string? schema, string? procedure) =>
        SQLProceduresW(stmt, catalog, Len(catalog), schema, Len(schema), procedure, Len(procedure));
    public short ProcedureColumns(nint stmt, string? catalog, string? schema, string? procedure, string? column) =>
        SQLProcedureColumnsW(stmt, catalog, Len(catalog), schema, Len(schema), procedure, Len(procedure), column, Len(column));
    public short NumResultCols(nint stmt, out short columns) => SQLNumResultCols(stmt, out columns);
    public short Fetch(nint stmt) => SQLFetch(stmt);
    public short GetData(nint stmt, ushort column, Span<byte> buffer, out nint indicator) => SQLGetData(stmt, column, -8, buffer, buffer.Length, out indicator);   // SQL_C_WCHAR
    public short Cancel(nint stmt) => SQLCancel(stmt);

    public string DiagState(short handleType, nint handle)
    {
        try
        {
            // Room for 4-byte SQLWCHAR so a UTF-32 driver manager cannot overrun the buffers; only UTF-16 is decoded.
            Span<byte> state = stackalloc byte[24];
            Span<byte> message = stackalloc byte[64];
            short ret = SQLGetDiagRecW(handleType, handle, 1, state, out _, message, 16, out _);
            if (ret is not (0 or 1)) return "HY000";
            string text = System.Text.Encoding.Unicode.GetString(state[..10]);
            return text.Length == 5 && text.All(char.IsAsciiLetterOrDigit) ? text : "HY000";
        }
        catch (Exception) { return "HY000"; }
    }
}
