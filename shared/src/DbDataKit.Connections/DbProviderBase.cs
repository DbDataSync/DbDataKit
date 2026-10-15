using System.Data.Common;
using System.Diagnostics;
using DbDataKit.Dialect;
using DbDataKit.Secrets;

namespace DbDataKit.Connections;

/// <summary>
/// Shared provider behaviour: dialect lookup, open with the read-only recipe applied, failure classification (the driver exception
/// never leaves; callers get a <see cref="ConnectionFailureException"/>), and a connection test.
/// </summary>
public abstract class DbProviderBase(
    EngineId engine, IConnectionStringComposer composer, ReadOnlySession readOnly,
    IFailureMapper? failureMapper = null, DialectCatalog? dialects = null, IRedactor? redactor = null) : IDbProvider
{
    private readonly FailureClassifier _classifier = new(failureMapper is null ? [] : [failureMapper], redactor);
    private readonly DialectCatalog _dialects = dialects ?? DialectCatalog.Builtin;

    public EngineId Engine { get; } = engine;
    public IConnectionStringComposer Composer { get; } = composer;
    public ReadOnlySession ReadOnly { get; } = readOnly;
    public IFailureMapper FailureMapper { get; } = failureMapper ?? new NoMapper();

    public virtual EngineCaps Caps => _dialects.TryGet(Engine, out var d) ? d!.Caps : EngineCaps.None;

    /// <summary>The statement <see cref="TestAsync"/> runs. Oracle-style engines override it.</summary>
    protected virtual string TestSql => "SELECT 1";

    public virtual ISqlDialect Dialect(ConnectionDefinition definition)
    {
        var id = definition.Dialect ?? definition.Engine;
        if (_dialects.TryGet(new EngineId(id), out var d)) return d!;
        int colon = id.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && _dialects.TryGet(new EngineId(id[..colon]), out d)) return d!;
        return _dialects.TryGet(Engine, out d) ? d! : _dialects.Get(EngineId.Ansi);
    }

    /// <summary>Builds the provider's own (unopened) connection object from a finished connection string.</summary>
    protected abstract DbConnection NewConnection(string connectionString, ConnectionContext context);

    public virtual DbConnection Create(ConnectionContext context)
    {
        var connection = NewConnection(Composer.Compose(context), context);
        return connection;
    }

    public async Task<DbConnection> OpenAsync(ConnectionContext context, CancellationToken ct = default)
    {
        DbConnection? connection = null;
        try
        {
            connection = Create(context);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            if (context.ReadOnly) await ReadOnly.ApplyAsync(connection, ct).ConfigureAwait(false);
            return connection;
        }
        catch (Exception e) when (e is not (ConnectionPolicyException or ConnectionFailureException))
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            if (e is OperationCanceledException && ct.IsCancellationRequested) throw;
            throw _classifier.ToException(e, Engine);
        }
        catch
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ConnectionTestResult> TestAsync(ConnectionContext context, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await using var c = await OpenAsync(context, ct).ConfigureAwait(false);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = TestSql;
            cmd.CommandTimeout = context.Definition.Timeouts.ConnectS;
            await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return new ConnectionTestResult(true, sw.Elapsed, null, c.ServerVersion);
        }
        catch (ConnectionFailureException e)
        {
            return new ConnectionTestResult(false, sw.Elapsed, e.Failure);
        }
        catch (Exception e) when (e is not (OperationCanceledException or ConnectionPolicyException))
        {
            return new ConnectionTestResult(false, sw.Elapsed, _classifier.Classify(e, Engine));
        }
    }

    private sealed class NoMapper : IFailureMapper
    {
        public ConnectionFailure? Map(Exception exception, EngineId engine) => null;
    }
}

/// <summary>A provider over any <see cref="DbProviderFactory"/> with a configurable composer: the built-in generic ADO.NET path.</summary>
public class GenericAdoProvider(
    EngineId engine, DbProviderFactory factory, IConnectionStringComposer composer, ReadOnlySession? readOnly = null,
    IFailureMapper? failureMapper = null, DialectCatalog? dialects = null)
    : DbProviderBase(engine, composer, readOnly ?? ReadOnlySession.NoneAvailable(engine.Value, "No session level recipe for a generic provider; use a read-only login."), failureMapper, dialects)
{
    protected override DbConnection NewConnection(string connectionString, ConnectionContext context)
    {
        var c = factory.CreateConnection() ?? throw new InvalidOperationException("The provider factory did not create a connection.");
        c.ConnectionString = connectionString;
        return c;
    }
}
