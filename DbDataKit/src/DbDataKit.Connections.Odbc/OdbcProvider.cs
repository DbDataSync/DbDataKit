using System.Data.Common;
using System.Data.Odbc;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using DbDataKit.Dialect;

namespace DbDataKit.Connections.Odbc;

/// <summary>Maps <see cref="OdbcException"/> by the first diagnostic record's SQLSTATE. Code is <c>state/native error</c>; the message text is never used.</summary>
public sealed class OdbcFailureMapper : IFailureMapper
{
    public ConnectionFailure? Map(Exception exception, EngineId engine)
    {
        if (exception is not OdbcException odbc) return null;
        string? state = null; int native = 0;
        foreach (OdbcError e in odbc.Errors) { state = e.SQLState; native = e.NativeError; break; }
        var kind = SqlStateMap.Classify(state) ?? ConnectionFailureKind.Other;
        return new ConnectionFailure(kind, state is null ? null : $"{state}/{native.ToString(CultureInfo.InvariantCulture)}", engine);
    }
}

public sealed record OdbcAccessModeResult(bool Applied, string Reason);

/// <summary>
/// Best-effort <c>SQL_ATTR_ACCESS_MODE=SQL_MODE_READ_ONLY</c>. System.Data.Odbc has no API for connection attributes, so this reads the
/// driver manager handle of an open connection (an internal property: if the shape changes it reports "not applied" instead of failing) and
/// calls <c>SQLSetConnectAttr</c>. Drivers may ignore the attribute (it is a hint for most), so the read login is still the control.
/// </summary>
public static unsafe class OdbcAccessMode
{
    private const int SqlAttrAccessMode = 101;
    private const int SqlModeReadOnly = 1;
    private const int SqlIsUInteger = -5;

    public static OdbcAccessModeResult TryApplyReadOnly(DbConnection connection)
    {
        if (connection is not OdbcConnection odbc) return new(false, "not an OdbcConnection");
        if (odbc.State != System.Data.ConnectionState.Open) return new(false, "the connection is not open");
        try
        {
            var prop = typeof(OdbcConnection).GetProperty("ConnectionHandle", BindingFlags.Instance | BindingFlags.NonPublic);
            if (prop?.GetValue(odbc) is not SafeHandle handle || handle.IsInvalid) return new(false, "the driver manager handle is not available");
            var fn = Resolve();
            if (fn == null) return new(false, "the ODBC driver manager library could not be loaded");
            bool added = false;
            try
            {
                handle.DangerousAddRef(ref added);
                short rc = fn(handle.DangerousGetHandle(), SqlAttrAccessMode, (nint)SqlModeReadOnly, SqlIsUInteger);
                return rc is 0 or 1 ? new(true, "applied") : new(false, $"the driver did not accept SQL_ATTR_ACCESS_MODE (return code {rc})");
            }
            finally { if (added) handle.DangerousRelease(); }
        }
        catch (Exception e) when (e is TargetInvocationException or MethodAccessException or InvalidCastException or NotSupportedException or EntryPointNotFoundException)
        {
            return new(false, "SQL_ATTR_ACCESS_MODE could not be set (" + e.GetType().Name + ")");
        }
    }

    private static delegate* unmanaged<nint, int, nint, int, short> Resolve()
    {
        string[] names = OperatingSystem.IsWindows() ? ["odbc32.dll"]
            : OperatingSystem.IsMacOS() ? ["libodbc.2.dylib", "libiodbc.dylib"]
            : ["libodbc.so.2", "libodbc.so", "libiodbc.so.2"];
        foreach (var name in names)
            if (NativeLibrary.TryLoad(name, out var lib) && NativeLibrary.TryGetExport(lib, "SQLSetConnectAttr", out var export))
                return (delegate* unmanaged<nint, int, nint, int, short>)export;
        return null;
    }
}

/// <summary>The ODBC read-only recipe: driver keywords at compose time (see <see cref="OdbcComposer"/>) plus the access mode attribute after open.</summary>
public sealed class OdbcReadOnlySession() : ReadOnlySession(
    "odbc", ReadOnlyEnforcement.SessionDefault, [], null,
    "Driver keywords where the driver is known (SQL Server ApplicationIntent, PostgreSQL ReadOnly, DuckDB access_mode) and SQL_ATTR_ACCESS_MODE after open; both are hints. The read login is the control.")
{
    public OdbcAccessModeResult? LastResult { get; private set; }

    public override Task ApplyAsync(DbConnection connection, CancellationToken ct = default)
    {
        LastResult = OdbcAccessMode.TryApplyReadOnly(connection);
        return Task.CompletedTask;
    }
}

/// <summary>ODBC provider over System.Data.Odbc.</summary>
public sealed class OdbcProvider : DbProviderBase
{
    public OdbcProvider(string? fixedDriver = null, DialectCatalog? dialects = null)
        : base(new EngineId("odbc"), new OdbcComposer(fixedDriver), new OdbcReadOnlySession(), new OdbcFailureMapper(), dialects) { }

    protected override DbConnection NewConnection(string connectionString, ConnectionContext context) => new OdbcConnection(connectionString);

    /// <summary>
    /// A command for a <c>@name</c> (or other source style) statement: markers become <c>?</c>, parameters are bound in recorded
    /// order, and the command timeout is set. This is what Sync's generic ODBC path was missing (positional only).
    /// </summary>
    public static DbCommand CreateCommand(DbConnection connection, string sql, ISqlDialect source, IReadOnlyDictionary<string, object?> values, int commandTimeoutSeconds)
    {
        var positional = ParameterStyleAdapter.ToPositional(sql, source);
        var cmd = connection.CreateCommand();
        try
        {
            positional.Bind(cmd, values);
            cmd.CommandTimeout = commandTimeoutSeconds;
            return cmd;
        }
        catch
        {
            cmd.Dispose();
            throw;
        }
    }
}
