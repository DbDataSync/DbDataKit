using System.Globalization;
using DbDataKit.Connections;
using DbDataKit.Dialect;
using DbDataKit.Odbc.Metadata;
using DbDataKit.Secrets;

namespace DbDataProxy.Data;

/// <summary>Marks exceptions whose message the Proxy wrote itself (never driver text), so it may be shown to a client.</summary>
public interface IClientVisible;

/// <summary>A value or column type the Proxy cannot carry (an ODBC type to CAST, a parameter type).</summary>
public sealed class UnsupportedValueException(string message, Exception? inner = null) : NotSupportedException(message, inner), IClientVisible;

/// <summary>The statement and its parameters do not agree.</summary>
public sealed class ParameterMismatchException(string message) : ArgumentException(message), IClientVisible;

/// <summary>Told about every classified failure, with the redacted raw text when raw diagnostics are on (local logs only).</summary>
public interface IFailureSink
{
    void Failed(string source, ConnectionFailure failure);
}

/// <summary>
/// Turns whatever a driver throws while a statement runs or a result is read into a <see cref="ConnectionFailureException"/> (kind, code, fixed
/// public text; never the driver's message), reporting the redacted raw text to a local sink. Proxy-written messages, cancellations and
/// already-classified failures pass through unchanged.
/// </summary>
public sealed class FailureTranslator(
    IFailureMapper? mapper, EngineId engine, string source, IRedactor? redactor = null, bool includeRaw = false, IFailureSink? sink = null)
{
    private readonly FailureClassifier _classifier = new(mapper is null ? [] : [mapper], redactor, includeRaw);

    public EngineId Engine { get; } = engine;

    public Exception Translate(Exception ex)
    {
        switch (ex)
        {
            case ConnectionFailureException known:
                sink?.Failed(source, known.Failure);
                return ex;
            case OperationCanceledException or ConnectionPolicyException or IClientVisible or OdbcPolicyViolationException or OdbcResultTruncatedException or Grpc.Core.RpcException:
                return ex;
            case OdbcCatalogException catalog:
                return Report(new ConnectionFailure(
                    SqlStateMap.Classify(catalog.SqlState) ?? ConnectionFailureKind.Other, catalog.SqlState, Engine, includeRaw ? Redact(catalog.ToString()) : null));
            default:
                return Report(_classifier.Classify(ex, Engine));
        }
    }

    private string Redact(string text) => redactor is null ? text : redactor.Redact(text);

    private ConnectionFailureException Report(ConnectionFailure failure)
    {
        sink?.Failed(source, failure);
        return new ConnectionFailureException(failure);
    }

    /// <summary>The failure carried by a worker's status trailers, or null when the call failed some other way.</summary>
    public static ConnectionFailure? FromTrailers(Grpc.Core.Metadata trailers, EngineId engine)
    {
        var kind = trailers.GetValue(KindHeader);
        if (kind is null || !Enum.TryParse<ConnectionFailureKind>(kind, out var parsed)) return null;
        return new ConnectionFailure(parsed, trailers.GetValue(CodeHeader), engine);
    }

    public static Grpc.Core.Metadata ToTrailers(ConnectionFailure failure)
    {
        var trailers = new Grpc.Core.Metadata { { KindHeader, failure.Kind.ToString() } };
        if (failure.Code is not null) trailers.Add(CodeHeader, failure.Code);
        return trailers;
    }

    public const string KindHeader = "x-failure-kind";
    public const string CodeHeader = "x-failure-code";

    /// <summary>Text for a log line: kind and code, no driver text.</summary>
    public static string Describe(ConnectionFailure failure) =>
        string.Create(CultureInfo.InvariantCulture, $"{failure.Kind}{(failure.Code is null ? "" : " " + failure.Code)}");
}
