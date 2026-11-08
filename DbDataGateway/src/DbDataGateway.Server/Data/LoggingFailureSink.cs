using DbDataKit.Connections;
using DbDataGateway.Data;

namespace DbDataGateway.Server.Data;

/// <summary>
/// Local log for classified database failures. The line always has the failure kind and engine code; the driver's own text (already scrubbed by the
/// shared redactor, and again by the redacting logger) follows when <c>Gateway:LogDriverErrors</c> is on. Clients never receive any of it.
/// </summary>
public sealed class LoggingFailureSink(ILogger log) : IFailureSink
{
    public void Failed(string source, ConnectionFailure failure)
    {
        if (failure.Raw is { Length: > 0 } raw)
            log.LogWarning("Data source {Source} failed: {Failure}. Driver text: {Raw}", source, FailureTranslator.Describe(failure), raw);
        else
            log.LogWarning("Data source {Source} failed: {Failure}", source, FailureTranslator.Describe(failure));
    }
}
