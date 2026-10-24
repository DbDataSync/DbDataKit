using System.Data.Common;
using DbDataKit.Connections;
using DbDataKit.Dialect;
using DbDataKit.Secrets;
using DbDataSync.Core.Config;

namespace DbDataSync.Core.Sql;

/// <summary>
/// What an operator sees when a database says no (plan 13, A10). A failure that came from a driver is classified by the shared
/// <see cref="FailureClassifier"/> and shown as its fixed public message plus the engine's error code
/// (<c>A secure connection could not be established. [sqlserver -2146893019]</c>); the driver's own text is logged, redacted, and
/// shown only while <see cref="IncludeDetails"/> is on. Anything that did not come from a driver (Sync's own validation messages)
/// passes through unchanged.
/// </summary>
public static class DriverFailures
{
    private static readonly Dictionary<string, IFailureMapper> Mappers = new(StringComparer.OrdinalIgnoreCase);
    private static volatile FailureClassifier _classifier = new();
    private static volatile Action<string>? _log;
    private static readonly Redactor Redaction = new();

    /// <summary>True when the redacted driver text is appended to the public message (the <c>Diagnostics:DriverDetails</c> setting).</summary>
    public static bool IncludeDetails { get; set; }

    /// <summary>Engine id (the shared <see cref="EngineId"/> value) for a Sync driver id.</summary>
    public static EngineId EngineFor(string? driverType) => driverType switch
    {
        DriverIds.MsSql => EngineId.SqlServer,
        DriverIds.Postgres => EngineId.Postgres,
        null or "" => new EngineId("db"),
        _ => new EngineId(driverType.ToLowerInvariant()),
    };

    /// <summary>Registers an engine's mapper (from <c>DbDataKit.Connections.*</c>) </summary>
    public static void Configure(IEnumerable<KeyValuePair<EngineId, IFailureMapper>> mappers, bool includeDetails)
    {
        lock (Mappers)
        {
            Mappers.Clear();
            foreach (var (engine, mapper) in mappers) Mappers[engine.Value] = mapper;
            _classifier = new FailureClassifier(Mappers.Select(m => new Guarded(new EngineId(m.Key), m.Value)).ToList());
        }
        IncludeDetails = includeDetails;
    }

    /// <summary>Where the driver's own redacted text goes (the host's log), once there is one.</summary>
    public static void SetLog(Action<string>? log) => _log = log;

    /// <summary>Makes a literal secret (a resolved credential) scrub from the logged and detailed text.</summary>
    public static void Register(string? secret)
    {
        if (!string.IsNullOrEmpty(secret)) Redaction.Register(secret);
    }

    /// <summary>The text for an exception raised while talking to a database of <paramref name="driverType"/>.</summary>
    public static string Describe(Exception exception, string? driverType = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (!CameFromADriver(exception)) return exception.Message;

        var engine = EngineFor(driverType);
        var failure = _classifier.Classify(exception, engine);
        // A SQLSTATE the shared map does not know is still the most useful code to show: better than the exception's type name.
        if (failure.Kind == ConnectionFailureKind.Other && Innermost(exception) is DbException { SqlState: { Length: > 0 } state })
            failure = new ConnectionFailure(ConnectionFailureKind.Other, state, engine);
        string detail = Redaction.Redact(exception.ToString());
        _log?.Invoke($"Database failure {failure.PublicMessage}: {detail}");
        return IncludeDetails ? failure.PublicMessage + " " + Redaction.Redact(Innermost(exception).Message) : failure.PublicMessage;
    }

    /// <summary>The stack-trace-bearing detail stored with a failed run: complete for Sync's own errors, withheld for a driver's unless details are on.</summary>
    public static string? Detail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return CameFromADriver(exception) && !IncludeDetails ? null : Redaction.Redact(exception.ToString());
    }

    /// <summary>True for a driver exception, or a wrapper that holds one.</summary>
    public static bool CameFromADriver(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
            if (e is DbException or System.Net.Sockets.SocketException or System.Security.Authentication.AuthenticationException) return true;
        return false;
    }

    private static Exception Innermost(Exception e)
    {
        var found = e;
        for (var x = e; x is not null; x = x.InnerException) if (x is DbException) found = x;
        return found;
    }

    /// <summary>A mapper whose package is not installed (a driver library not yet restored) answers "not mine" instead of failing the classification.</summary>
    private sealed class Guarded(EngineId own, IFailureMapper inner) : IFailureMapper
    {
        public ConnectionFailure? Map(Exception exception, EngineId engine)
        {
            if (engine != own && engine.Value != "db") return null;   // "db": the caller did not know the engine, so each mapper looks at the exception type
            try { return inner.Map(exception, engine); }
            catch (Exception e) when (e is FileNotFoundException or FileLoadException or TypeLoadException) { return null; }
        }
    }
}
