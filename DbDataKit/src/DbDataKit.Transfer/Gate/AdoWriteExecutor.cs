using System.Data;
using System.Data.Common;
using System.Globalization;
using DbDataKit.Dialect;

namespace DbDataKit.Transfer;

/// <summary>Executes one statement. Implemented by the ADO executor; bulk writers that fall back to SQL take one of these.</summary>
public interface IStatementExecutor
{
    Task<long> ExecuteAsync(SqlStatement statement, CancellationToken ct);
    /// <summary>First column of the first row, or null.</summary>
    Task<object?> ScalarAsync(SqlStatement statement, CancellationToken ct);
}

/// <summary>The write side of a connection: statements, transaction control and application locks. Only the gate calls it.</summary>
public interface IWriteExecutor : IStatementExecutor, IAsyncDisposable
{
    Task BeginAsync(CancellationToken ct);
    Task CommitAsync(CancellationToken ct);
    /// <summary>Rolls back whatever is open; never throws for "nothing to roll back".</summary>
    Task RollbackAsync(CancellationToken ct);
    Task<bool> TryLockAsync(string resource, bool shared, CancellationToken ct);
    Task UnlockAsync(string resource, bool shared, CancellationToken ct);
}

/// <summary>
/// ADO.NET executor. With the read-only conformance reader and the bulk writers, this is one of the few files allowed to touch command APIs
/// (see <see cref="SourceInvariants"/>).
/// </summary>
public sealed class AdoWriteExecutor(DbConnection connection, ISqlDialect dialect, TimeSpan? commandTimeout = null) : IWriteExecutor
{
    private DbTransaction? _tx;

    public async Task BeginAsync(CancellationToken ct)
    {
        if (_tx is not null) throw new InvalidOperationException("a transaction is already open");
        _tx = await connection.BeginTransactionAsync(ct);
    }

    public async Task CommitAsync(CancellationToken ct)
    {
        var tx = _tx ?? throw new InvalidOperationException("no transaction is open");
        _tx = null;
        await using (tx) await tx.CommitAsync(ct);
    }

    public async Task RollbackAsync(CancellationToken ct)
    {
        var tx = _tx;
        _tx = null;
        if (tx is null) return;
        await using (tx) { try { await tx.RollbackAsync(ct); } catch (Exception ex) when (ex is DbException or InvalidOperationException) { /* connection already unusable */ } }
    }

    public async Task<long> ExecuteAsync(SqlStatement statement, CancellationToken ct)
    {
        await using var cmd = Create(statement);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<object?> ScalarAsync(SqlStatement statement, CancellationToken ct)
    {
        await using var cmd = Create(statement);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is DBNull ? null : v;
    }

    private DbCommand Create(SqlStatement statement)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = statement.Text;
        cmd.Transaction = _tx;
        cmd.CommandTimeout = commandTimeout is { } t ? (int)t.TotalSeconds : 0;
        Bind(cmd, dialect, statement.Parameters);
        return cmd;
    }

    /// <summary>
    /// Adds parameters in statement order. A named marker (<c>@name</c>, <c>:name</c>) sets the name without its prefix character; numbered (<c>$1</c>)
    /// and positional (<c>?</c>) markers are unnamed. The marker decides, not the dialect, so statements built with a caller's own placeholder text
    /// (<see cref="StatementSyntax"/>) bind as they read.
    /// </summary>
    internal static void Bind(DbCommand cmd, ISqlDialect dialect, IReadOnlyList<SqlParameter> parameters)
    {
        foreach (var p in parameters)
        {
            var dp = cmd.CreateParameter();
            if (p.Marker is ['@' or ':', _, ..]) dp.ParameterName = p.Marker[1..];
            if (ParameterValues.DbTypeFor(dialect.Id, p.Type) is { } dbType) dp.DbType = dbType;
            dp.Value = ParameterValues.Adapt(dialect.Id, p.Value) ?? DBNull.Value;
            cmd.Parameters.Add(dp);
        }
    }

    /// <summary>Date and time kinds are left to the provider, which infers them from DateOnly/TimeOnly/DateTime/DateTimeOffset.</summary>
    internal static DbType? DbTypeOf(CanonicalType t) => t.Kind switch
    {
        CanonicalKind.Boolean => DbType.Boolean,
        CanonicalKind.Int8 => t.Unsigned == true ? DbType.Byte : DbType.SByte,
        CanonicalKind.Int16 => t.Unsigned == true ? DbType.UInt16 : DbType.Int16,
        CanonicalKind.Int32 => t.Unsigned == true ? DbType.UInt32 : DbType.Int32,
        CanonicalKind.Int64 => t.Unsigned == true ? DbType.UInt64 : DbType.Int64,
        CanonicalKind.Decimal => DbType.Decimal,
        CanonicalKind.Float => DbType.Single,
        CanonicalKind.Double => DbType.Double,
        CanonicalKind.String or CanonicalKind.Json or CanonicalKind.Xml => DbType.String,
        CanonicalKind.Binary => DbType.Binary,
        CanonicalKind.Guid => DbType.Guid,
        _ => null,
    };

    public async Task<bool> TryLockAsync(string resource, bool shared, CancellationToken ct)
    {
        var (text, isBool) = LockText(resource, shared, acquire: true);
        var result = await RunLockAsync(text, resource, ct);
        return isBool ? Convert.ToBoolean(result, CultureInfo.InvariantCulture) : Convert.ToInt32(result, CultureInfo.InvariantCulture) >= 0;
    }

    public async Task UnlockAsync(string resource, bool shared, CancellationToken ct)
    {
        var (text, _) = LockText(resource, shared, acquire: false);
        await RunLockAsync(text, resource, ct);
    }

    private (string Text, bool IsBool) LockText(string resource, bool shared, bool acquire)
    {
        var p = dialect.ParameterName(0, "res");
        if (dialect.Id == EngineId.Postgres)
        {
            var fn = (acquire ? "pg_try_advisory_lock" : "pg_advisory_unlock") + (shared ? "_shared" : "");
            return ($"SELECT {fn}(hashtextextended({p}, 0))", true);
        }
        if (dialect.Id == EngineId.SqlServer)
        {
            return acquire
                ? ($"DECLARE @r int; EXEC @r = sp_getapplock @Resource = {p}, @LockMode = N'{(shared ? "Shared" : "Exclusive")}', @LockOwner = N'Session', @LockTimeout = 0; SELECT @r;", false)
                : ($"EXEC sp_releaseapplock @Resource = {p}, @LockOwner = N'Session';", false);
        }
        throw new NotSupportedException($"application locks are not implemented for {dialect.Id}");
    }

    private async Task<object?> RunLockAsync(string text, string resource, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = text;
        cmd.Transaction = _tx;
        Bind(cmd, dialect, [new SqlParameter(dialect.ParameterName(0, "res"), "res", CanonicalType.String(), resource)]);
        return await cmd.ExecuteScalarAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await RollbackAsync(CancellationToken.None);
        await connection.DisposeAsync();
    }
}
