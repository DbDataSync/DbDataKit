using System.Data.Common;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using DbDataKit.Dialect;
using DbDataKit.Secrets;

namespace DbDataKit.Connections;

public enum ConnectionFailureKind { Login, Network, Timeout, Tls, Permission, Syntax, Unsupported, Other }

/// <summary>
/// A classified failure. <see cref="PublicMessage"/> is fixed text per <see cref="Kind"/> plus the code; it is never raw driver output.
/// <see cref="Raw"/> is set only when the classifier was created with raw diagnostics enabled, and then passed through the redactor.
/// </summary>
public sealed class ConnectionFailure(ConnectionFailureKind kind, string? code, EngineId? engine, string? raw = null)
{
    public ConnectionFailureKind Kind { get; } = kind;
    /// <summary>Engine error number, SQLSTATE or exception type name. Safe to log.</summary>
    public string? Code { get; } = code;
    public EngineId? Engine { get; } = engine;
    /// <summary>Local diagnostics only (explicit flag, redacted). Never put this in a client response.</summary>
    public string? Raw { get; } = raw;

    public static string TextFor(ConnectionFailureKind kind) => kind switch
    {
        ConnectionFailureKind.Login => "The login was rejected.",
        ConnectionFailureKind.Network => "The server could not be reached.",
        ConnectionFailureKind.Timeout => "The operation timed out.",
        ConnectionFailureKind.Tls => "A secure connection could not be established.",
        ConnectionFailureKind.Permission => "The login does not have permission for this operation.",
        ConnectionFailureKind.Syntax => "The statement was not accepted by the server.",
        ConnectionFailureKind.Unsupported => "The operation is not supported by this driver or server.",
        _ => "The database operation failed.",
    };

    public string PublicMessage =>
        TextFor(Kind) + (Code is null && Engine is null ? "" : $" [{Engine?.Value ?? "db"}{(Code is null ? "" : " " + Code)}]");

    public override string ToString() => PublicMessage;
}

/// <summary>Thrown by providers instead of the driver exception. The inner exception is deliberately not kept.</summary>
public sealed class ConnectionFailureException(ConnectionFailure failure) : Exception(failure.PublicMessage)
{
    public ConnectionFailure Failure { get; } = failure;
}

/// <summary>Engine-specific mapping (SqlException numbers, Npgsql SQLSTATE, OdbcException states). Return null to defer to the next mapper.</summary>
public interface IFailureMapper
{
    ConnectionFailure? Map(Exception exception, EngineId engine);
}

/// <summary>Maps a SQLSTATE (or ODBC state) to a kind. Shared by the generic classifier and engine mappers.</summary>
public static class SqlStateMap
{
    public static ConnectionFailureKind? Classify(string? state)
    {
        if (string.IsNullOrEmpty(state) || state.Length < 2) return null;
        state = state.ToUpperInvariant();
        return state switch
        {
            "42501" => ConnectionFailureKind.Permission,
            "HYT00" or "HYT01" or "57014" or "HYT02" => ConnectionFailureKind.Timeout,
            "0A000" or "HYC00" or "IM001" or "IM002" or "IM003" or "IM004" => ConnectionFailureKind.Unsupported,
            "08P01" => ConnectionFailureKind.Other,
            _ when state.StartsWith("28", StringComparison.Ordinal) => ConnectionFailureKind.Login,
            _ when state.StartsWith("08", StringComparison.Ordinal) => ConnectionFailureKind.Network,
            "53300" or "57P03" or "57P01" or "57P02" => ConnectionFailureKind.Network,
            _ when state.StartsWith("42", StringComparison.Ordinal) => ConnectionFailureKind.Syntax,
            _ when state.StartsWith("37", StringComparison.Ordinal) => ConnectionFailureKind.Syntax,
            _ => null,
        };
    }
}

/// <summary>
/// Turns any exception from a driver into a <see cref="ConnectionFailure"/>: engine mappers first, then SQLSTATE, then exception types
/// (socket, TLS, timeout), then <see cref="ConnectionFailureKind.Other"/> with the exception type name as the code.
/// </summary>
public sealed class FailureClassifier(IEnumerable<IFailureMapper>? mappers = null, IRedactor? redactor = null, bool includeRaw = false)
{
    private readonly IFailureMapper[] _mappers = (mappers ?? []).ToArray();

    public ConnectionFailure Classify(Exception exception, EngineId engine)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var chain = Unwrap(exception).ToList();
        foreach (var ex in chain)
            foreach (var mapper in _mappers)
                if (mapper.Map(ex, engine) is { } mapped) return WithRaw(mapped, exception);

        foreach (var ex in chain)
            if (Generic(ex, engine) is { } generic) return WithRaw(generic, exception);

        return WithRaw(new ConnectionFailure(ConnectionFailureKind.Other, exception.GetType().Name, engine), exception);
    }

    /// <summary>Wraps <see cref="Classify"/> in the exception providers throw.</summary>
    public ConnectionFailureException ToException(Exception exception, EngineId engine) => new(Classify(exception, engine));

    private ConnectionFailure WithRaw(ConnectionFailure f, Exception original)
    {
        if (!includeRaw) return f;
        var text = original.ToString();
        if (redactor is not null) text = redactor.Redact(text);
        return new ConnectionFailure(f.Kind, f.Code, f.Engine, text);
    }

    private static ConnectionFailure? Generic(Exception ex, EngineId engine)
    {
        switch (ex)
        {
            case TimeoutException: return new(ConnectionFailureKind.Timeout, nameof(TimeoutException), engine);
            case OperationCanceledException: return new(ConnectionFailureKind.Other, "canceled", engine);
            case AuthenticationException or CryptographicException: return new(ConnectionFailureKind.Tls, ex.GetType().Name, engine);
            case SocketException se: return new(ConnectionFailureKind.Network, "socket-" + (int)se.SocketErrorCode, engine);
            case DbException db:
                {
                    var kind = SqlStateMap.Classify(db.SqlState);
                    return kind is null ? null : new ConnectionFailure(kind.Value, db.SqlState, engine);
                }
            case IOException: return new(ConnectionFailureKind.Network, nameof(IOException), engine);
            default: return null;
        }
    }

    private static IEnumerable<Exception> Unwrap(Exception ex)
    {
        if (ex is AggregateException agg)
        {
            foreach (var inner in agg.InnerExceptions)
                foreach (var x in Unwrap(inner)) yield return x;
            yield break;
        }
        yield return ex;
        if (ex.InnerException is { } next)
            foreach (var x in Unwrap(next)) yield return x;
    }
}
