using DbDataKit.Connections;
using DbDataKit.Monitoring;

namespace DbDataKit.Runner;

/// <summary>How a task failed (plan 28a R02, DR8).</summary>
public enum TaskFailureKind
{
    /// <summary>Worth trying again: a connection that could not be reached, a timeout against a database.</summary>
    Temporary,
    /// <summary>Trying again changes nothing: a rejected login, a statement the server refuses, a task that reported its own failure as final.</summary>
    Permanent,
    /// <summary>Stopped because it was asked to (an operator, a failure policy).</summary>
    Cancelled,
    /// <summary>Ran past the task's time limit and was stopped.</summary>
    TimedOut,
    /// <summary>The process ended without the end message (a crash, a kill, a non-zero exit code).</summary>
    ProcessExited,
    /// <summary>The task broke the protocol (spoke another version, ended twice, sent something the runner cannot read).</summary>
    ProtocolViolation,
    /// <summary>Went over a memory or other resource limit and was stopped.</summary>
    LimitExceeded,
}

/// <summary>
/// A classified failure. <see cref="Retryable"/> comes from <see cref="Kind"/> alone. <see cref="Code"/> is the name of the
/// <see cref="ConnectionFailureKind"/> when the failure came from a connection, so the finer class is not lost. The message never holds a secret.
/// </summary>
public sealed record TaskFailure(TaskFailureKind Kind, string Message, string? Item = null, string? Code = null)
{
    /// <summary>Whether a retry can help (DR8): a temporary failure, a timeout, a process that went away. Never a permanent one, a cancel, a protocol violation or a limit.</summary>
    public bool Retryable => IsRetryable(Kind);

    public static bool IsRetryable(TaskFailureKind kind) => kind switch
    {
        TaskFailureKind.Temporary or TaskFailureKind.TimedOut or TaskFailureKind.ProcessExited => true,
        TaskFailureKind.Permanent or TaskFailureKind.Cancelled or TaskFailureKind.ProtocolViolation or TaskFailureKind.LimitExceeded => false,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A failure kind says whether it is retryable."),
    };

    /// <summary>
    /// The one place a connection's classification becomes a task's: a network failure and a database timeout are temporary; every other kind
    /// (login, TLS, permission, syntax, unsupported, not found, other) is permanent, because retrying an unknown or configuration problem only repeats it.
    /// </summary>
    public static TaskFailureKind KindOf(ConnectionFailureKind kind) => kind switch
    {
        ConnectionFailureKind.Network or ConnectionFailureKind.Timeout or ConnectionFailureKind.RateLimited or ConnectionFailureKind.Unavailable => TaskFailureKind.Temporary,
        ConnectionFailureKind.Login or ConnectionFailureKind.Tls or ConnectionFailureKind.Permission or ConnectionFailureKind.Syntax
            or ConnectionFailureKind.Unsupported or ConnectionFailureKind.Other or ConnectionFailureKind.NotFound => TaskFailureKind.Permanent,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "A connection failure kind is classified as temporary or permanent."),
    };

    /// <summary>The failure a connection's classification gives. The message is the connection's public text, never raw driver output.</summary>
    public static TaskFailure From(ConnectionFailure failure, string? item = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return new TaskFailure(KindOf(failure.Kind), failure.PublicMessage, item, failure.Kind.ToString());
    }

    /// <summary>What the run store records.</summary>
    public RunFailure ToRunFailure() => new(Code is null ? Kind.ToString() : $"{Kind}:{Code}", Message);
}
