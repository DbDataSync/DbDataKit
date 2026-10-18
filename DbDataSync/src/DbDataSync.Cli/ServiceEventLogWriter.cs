using System.Diagnostics;

namespace DbDataSync.Cli;

/// <summary>Where a service event-log entry ends up. The real one is <c>EventLog.WriteEntry</c> under the registered source
/// (see <see cref="WindowsServiceEventLog"/>); tests supply a fake, so the logic here runs on every platform without touching the
/// machine's real Application log.</summary>
internal interface IEventLogSink
{
    void Write(string message, EventLogEntryType type);
}

/// <summary>
/// What goes into the event log, separate from the Windows call that stores it: the text of a startup failure and the entry type of
/// each kind of entry. The part that is the product's own behaviour, so it is tested deterministically; whether Windows stores an
/// entry written under a registered source is the platform's contract, not ours.
/// </summary>
internal sealed class ServiceEventLogWriter(IEventLogSink sink)
{
    /// <summary>The text of a startup failure: the given message, or the exception's own type and message. One definition for the
    /// event log and the console, so a failure reads the same wherever it is reported.</summary>
    internal static string FailureText(Exception exception, string? message) =>
        message ?? $"{exception.GetType().Name}: {exception.Message}";

    internal void WriteError(Exception exception, string? message = null) =>
        sink.Write(FailureText(exception, message), EventLogEntryType.Error);

    internal void WriteInformation(string message) =>
        sink.Write(message, EventLogEntryType.Information);
}
