using System.Runtime.CompilerServices;
using System.Text;

namespace DbDataKit.Odbc.Metadata;

/// <summary>
/// The catalog functions over a real (or, in tests, scripted) driver manager. It opens a connection of its own from the
/// connection string (System.Data.Odbc keeps its handle private) and calls only catalog functions and SQLGetInfo. The connection
/// string is checked against the <see cref="OdbcConnectionPolicy"/> first; the login and statement timeouts, the row cap and
/// cancellation (SQLCancel) come from <see cref="OdbcCatalogOptions"/>.
/// </summary>
internal sealed class OdbcNativeFunctions(string connectionString, OdbcCatalogOptions? options = null, IOdbcApi? api = null) : IOdbcCatalogFunctions
{
    private const short HandleEnv = 1, HandleDbc = 2, HandleStmt = 3;
    private const int AttrOdbcVersion = 200, AttrLoginTimeout = 103, AttrQueryTimeout = 0, StreamBatch = 256, MaxInfoBytes = short.MaxValue - 1;
    private const ushort InfoDriverName = 6, InfoDbmsName = 17;

    private readonly OdbcCatalogOptions options = options ?? OdbcCatalogOptions.Default;
    private readonly IOdbcApi api = api ?? OdbcNativeApi.Instance;
    private nint environment, connection;
    private readonly SemaphoreSlim one = new(1, 1);

    private static bool Ok(short ret) => ret is 0 or 1;   // SQL_SUCCESS, SQL_SUCCESS_WITH_INFO

    private void Connect()
    {
        if (connection != 0) return;
        string text = OdbcConnectionString.Normalize(connectionString);
        options.Policy.Validate(text);

        if (environment == 0)
        {
            if (!Ok(api.AllocHandle(HandleEnv, 0, out nint env))) throw new OdbcCatalogException("SQLAllocHandle", "HY001");
            environment = env;
            api.SetEnvAttr(env, AttrOdbcVersion, 3);                          // SQL_OV_ODBC3
        }
        if (!Ok(api.AllocHandle(HandleDbc, environment, out nint dbc))) throw new OdbcCatalogException("SQLAllocHandle", api.DiagState(HandleEnv, environment));
        if (options.LoginTimeout is { } login) api.SetConnectAttr(dbc, AttrLoginTimeout, Seconds(login));
        if (!Ok(api.DriverConnect(dbc, text)))
        {
            string state = api.DiagState(HandleDbc, dbc);
            api.FreeHandle(HandleDbc, dbc);
            throw new OdbcCatalogException("SQLDriverConnect", state);
        }

        try { VerifyWideCharWidth(dbc); }
        catch
        {
            api.Disconnect(dbc);
            api.FreeHandle(HandleDbc, dbc);
            throw;
        }
        connection = dbc;
    }

    private static nint Seconds(TimeSpan value) => value <= TimeSpan.Zero ? 0 : (nint)Math.Min(int.MaxValue, Math.Ceiling(value.TotalSeconds));

    // SQLWCHAR is 2 bytes on Windows and in the default unixODBC build. Some unixODBC builds use 4 bytes; every wide string would then
    // come back as garbage, so ask for one known string and refuse to go on rather than return nonsense.
    private void VerifyWideCharWidth(nint dbc)
    {
        foreach (ushort info in new[] { InfoDbmsName, InfoDriverName })
        {
            var buffer = new byte[256];
            if (!Ok(api.GetInfo(dbc, info, buffer, out short length)) || length <= 0) continue;
            if (OdbcWideChar.LooksLikeFourByte(buffer, length))
                throw new OdbcConfigurationException(
                    "The ODBC driver manager uses 4-byte SQLWCHAR (a UTF-32 unixODBC build). DbDataKit.Odbc.Metadata reads UTF-16 (2-byte) SQLWCHAR only; " +
                    "use a driver manager built with the default 2-byte SQLWCHAR.");
            return;
        }
    }

    private async Task<T> SerializedAsync<T>(Func<T> work, CancellationToken cancellationToken)
    {
        await one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(work, cancellationToken).ConfigureAwait(false); }
        finally { one.Release(); }
    }

    // One catalog function: a statement handle for it, every row of its result as text (up to the cap), the handle freed.
    private Task<IReadOnlyList<string?[]>> RunAsync(string function, Func<nint, short> call, CancellationToken cancellationToken) =>
        SerializedAsync<IReadOnlyList<string?[]>>(() => Fetch(function, call, cancellationToken), cancellationToken);

    private nint OpenStatement(string function, Func<nint, short> call, CancellationToken cancellationToken)
    {
        Connect();
        if (!Ok(api.AllocHandle(HandleStmt, connection, out nint stmt))) throw new OdbcCatalogException(function, api.DiagState(HandleDbc, connection));
        try
        {
            // A driver that cannot set the timeout still answers; cancellation through SQLCancel does not depend on it.
            if (options.StatementTimeout is { } timeout && timeout > TimeSpan.Zero) api.SetStmtAttr(stmt, AttrQueryTimeout, Seconds(timeout));
            using var registration = CancelWith(stmt, cancellationToken);
            if (!Ok(call(stmt)))
            {
                var failure = new OdbcCatalogException(function, api.DiagState(HandleStmt, stmt));
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                throw failure;
            }
            return stmt;
        }
        catch
        {
            api.FreeHandle(HandleStmt, stmt);
            throw;
        }
    }

    // Asks the driver to abandon a call blocked in another thread; the failed call then surfaces as cancellation.
    private CancellationTokenRegistration CancelWith(nint stmt, CancellationToken cancellationToken) =>
        cancellationToken.Register(static state => { var (api, stmt) = ((IOdbcApi, nint))state!; try { api.Cancel(stmt); } catch (Exception) { /* best effort */ } }, (api, stmt));

    private OdbcRows Fetch(string function, Func<nint, short> call, CancellationToken cancellationToken)
    {
        nint stmt = OpenStatement(function, call, cancellationToken);
        try
        {
            using var registration = CancelWith(stmt, cancellationToken);
            try
            {
                api.NumResultCols(stmt, out short width);
                var rows = new List<string?[]>();
                var buffer = new byte[4096];
                int? cap = options.MaxRows;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    short ret = api.Fetch(stmt);
                    if (ret == 100) break;                                    // SQL_NO_DATA
                    if (!Ok(ret)) throw new OdbcCatalogException(function, api.DiagState(HandleStmt, stmt));
                    if (cap is { } max && rows.Count >= max) return new OdbcRows(rows, truncated: true);   // one more row exists
                    rows.Add(ReadRow(stmt, width, buffer, function));
                }
                return new OdbcRows(rows, truncated: false);
            }
            catch (OdbcCatalogException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
        finally { api.FreeHandle(HandleStmt, stmt); }
    }

    private async IAsyncEnumerable<string?[]> StreamAsync(string function, Func<nint, short> call, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            nint stmt = await Task.Run(() => OpenStatement(function, call, cancellationToken), cancellationToken).ConfigureAwait(false);
            try
            {
                using var registration = CancelWith(stmt, cancellationToken);
                short width = 0;
                api.NumResultCols(stmt, out width);
                bool done = false;
                while (!done)
                {
                    List<string?[]> batch;
                    try
                    {
                        batch = await Task.Run(() =>
                        {
                            var rows = new List<string?[]>(StreamBatch);
                            var buffer = new byte[4096];
                            while (rows.Count < StreamBatch)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                short ret = api.Fetch(stmt);
                                if (ret == 100) { done = true; break; }
                                if (!Ok(ret)) throw new OdbcCatalogException(function, api.DiagState(HandleStmt, stmt));
                                rows.Add(ReadRow(stmt, width, buffer, function));
                            }
                            return rows;
                        }, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OdbcCatalogException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                    foreach (var row in batch) yield return row;
                }
            }
            finally { api.FreeHandle(HandleStmt, stmt); }
        }
        finally { one.Release(); }
    }

    private string?[] ReadRow(nint stmt, short width, byte[] buffer, string function)
    {
        var row = new string?[width];
        for (ushort c = 1; c <= width; c++) row[c - 1] = Text(stmt, c, buffer, function);
        return row;
    }

    private string? Text(nint stmt, ushort column, byte[] buffer, string function)
    {
        var text = new StringBuilder();
        while (true)
        {
            short ret = api.GetData(stmt, column, buffer, out nint indicator);
            if (ret == 100) break;
            if (!Ok(ret)) throw new OdbcCatalogException(function, api.DiagState(HandleStmt, stmt));
            if (indicator == -1) return text.Length == 0 ? null : text.ToString();                   // SQL_NULL_DATA
            // Truncated (SUCCESS_WITH_INFO): a full buffer less the terminator; otherwise exactly what the driver reports.
            int available = ret == 1 && (indicator == -4 || indicator >= buffer.Length) ? buffer.Length - 2 : (int)Math.Min(indicator, buffer.Length - 2);
            text.Append(Encoding.Unicode.GetString(buffer, 0, Math.Max(0, available - available % 2)));
            if (ret == 0) break;
        }
        return text.ToString();
    }

    public Task<IReadOnlyList<string?[]>> TablesAsync(string? catalog, string? schema, string? table, string? types, CancellationToken cancellationToken) =>
        RunAsync("SQLTables", s => api.Tables(s, catalog, schema, table, types), cancellationToken);

    public Task<IReadOnlyList<string?[]>> ColumnsAsync(string? catalog, string? schema, string? table, string? column, CancellationToken cancellationToken) =>
        RunAsync("SQLColumns", s => api.Columns(s, catalog, schema, table, column), cancellationToken);

    public Task<IReadOnlyList<string?[]>> PrimaryKeysAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken) =>
        RunAsync("SQLPrimaryKeys", s => api.PrimaryKeys(s, catalog, schema, table), cancellationToken);

    public Task<IReadOnlyList<string?[]>> ForeignKeysAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken) =>
        RunAsync("SQLForeignKeys", s => api.ForeignKeys(s, catalog, schema, table), cancellationToken);

    public Task<IReadOnlyList<string?[]>> StatisticsAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken) =>
        RunAsync("SQLStatistics", s => api.Statistics(s, catalog, schema, table), cancellationToken);

    public Task<IReadOnlyList<string?[]>> TypeInfoAsync(int sqlType, CancellationToken cancellationToken) =>
        RunAsync("SQLGetTypeInfo", s => api.TypeInfo(s, (short)sqlType), cancellationToken);

    public Task<IReadOnlyList<string?[]>> ProceduresAsync(string? catalog, string? schema, string? procedure, CancellationToken cancellationToken) =>
        RunAsync("SQLProcedures", s => api.Procedures(s, catalog, schema, procedure), cancellationToken);

    public Task<IReadOnlyList<string?[]>> ProcedureColumnsAsync(string? catalog, string? schema, string? procedure, string? column, CancellationToken cancellationToken) =>
        RunAsync("SQLProcedureColumns", s => api.ProcedureColumns(s, catalog, schema, procedure, column), cancellationToken);

    public Task<IReadOnlyList<OdbcResultColumn>> DescribeQueryAsync(string sql, CancellationToken cancellationToken) =>
        SerializedAsync<IReadOnlyList<OdbcResultColumn>>(() =>
        {
            nint stmt = OpenStatement("SQLPrepare", s => api.Prepare(s, sql), cancellationToken);
            try
            {
                if (!Ok(api.NumResultCols(stmt, out short width))) throw new OdbcCatalogException("SQLNumResultCols", api.DiagState(HandleStmt, stmt));
                var columns = new List<OdbcResultColumn>(width);
                for (ushort c = 1; c <= width; c++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Ok(api.DescribeCol(stmt, c, out string name, out short sqlType, out long size, out short scale, out short nullable)))
                        throw new OdbcCatalogException("SQLDescribeCol", api.DiagState(HandleStmt, stmt));
                    // SQL_NO_NULLS 0, SQL_NULLABLE 1, SQL_NULLABLE_UNKNOWN 2. Size 0 means the driver does not say.
                    columns.Add(new OdbcResultColumn(c, name, sqlType, size > 0 ? (int)Math.Min(size, int.MaxValue) : null, scale, nullable switch { 0 => false, 1 => true, _ => null }));
                }
                return columns;
            }
            finally { api.FreeHandle(HandleStmt, stmt); }
        }, cancellationToken);

    public IAsyncEnumerable<string?[]> StreamTablesAsync(string? catalog, string? schema, string? table, string? types, CancellationToken cancellationToken) =>
        StreamAsync("SQLTables", s => api.Tables(s, catalog, schema, table, types), cancellationToken);

    public IAsyncEnumerable<string?[]> StreamColumnsAsync(string? catalog, string? schema, string? table, string? column, CancellationToken cancellationToken) =>
        StreamAsync("SQLColumns", s => api.Columns(s, catalog, schema, table, column), cancellationToken);

    // SQLGetInfo with a string result: start at 512 bytes and, when the driver reports a longer value, ask again with room for it.
    // The length is a SQLSMALLINT, so a value that needs more than 32,766 bytes cannot be read and is an error, not a truncation.
    public Task<string?> GetInfoStringAsync(ushort infoType, CancellationToken cancellationToken) =>
        SerializedAsync<string?>(() =>
        {
            Connect();
            int size = 512;
            while (true)
            {
                var buffer = new byte[size];
                if (!Ok(api.GetInfo(connection, infoType, buffer, out short length))) throw new OdbcCatalogException("SQLGetInfo", api.DiagState(HandleDbc, connection));
                if (length < 0) length = 0;
                if (length <= size - 2)
                    return Encoding.Unicode.GetString(buffer, 0, length - length % 2);
                int needed = length + 2;
                if (needed > MaxInfoBytes || size >= MaxInfoBytes) throw new OdbcCatalogException("SQLGetInfo", "01004");   // string data, right truncated
                size = needed + needed % 2;
            }
        }, cancellationToken);

    public Task<uint> GetInfoNumberAsync(ushort infoType, CancellationToken cancellationToken) =>
        SerializedAsync(() =>
        {
            Connect();
            var buffer = new byte[4];   // zeroed: a SQLUSMALLINT answer fills only the low two bytes
            if (!Ok(api.GetInfo(connection, infoType, buffer, out _))) throw new OdbcCatalogException("SQLGetInfo", api.DiagState(HandleDbc, connection));
            return BitConverter.ToUInt32(buffer, 0);
        }, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (connection != 0) { api.Disconnect(connection); api.FreeHandle(HandleDbc, connection); connection = 0; }
        if (environment != 0) { api.FreeHandle(HandleEnv, environment); environment = 0; }
        return ValueTask.CompletedTask;
    }
}

/// <summary>Detects a driver manager whose SQLWCHAR is 4 bytes wide by looking at a string it returned.</summary>
internal static class OdbcWideChar
{
    /// <summary>
    /// True when <paramref name="bytes"/> (the first <paramref name="reportedLength"/> bytes of a string SQLGetInfoW returned) cannot be
    /// UTF-16 because a NUL code unit appears before the end, which is what ASCII text looks like in UTF-32.
    /// </summary>
    public static bool LooksLikeFourByte(ReadOnlySpan<byte> bytes, int reportedLength)
    {
        int length = Math.Min(reportedLength, bytes.Length) & ~1;
        for (int i = 0; i + 1 < length; i += 2)
            if (bytes[i] == 0 && bytes[i + 1] == 0) return true;
        return false;
    }
}
