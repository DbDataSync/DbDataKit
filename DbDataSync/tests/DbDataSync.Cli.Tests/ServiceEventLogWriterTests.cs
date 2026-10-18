using System.Diagnostics;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// What the Windows service reports to the Event Log, tested against a fake sink so it runs on every platform and depends on no
/// machine state. The platform's half, that an entry written under the registered source is stored, is Windows' contract; the one
/// part of it that is ours, the source being registered against the Application log, is in <c>WindowsServiceEventLogTests</c>.
/// </summary>
public sealed class ServiceEventLogWriterTests
{
    private sealed class RecordingSink : IEventLogSink
    {
        public List<(string Message, EventLogEntryType Type)> Entries { get; } = [];
        public void Write(string message, EventLogEntryType type) => Entries.Add((message, type));
    }

    [Fact]
    public void WriteError_WithAMessage_WritesThatMessageAsAnError()
    {
        var sink = new RecordingSink();

        new ServiceEventLogWriter(sink).WriteError(new InvalidOperationException("ignored"), "a specific message");

        Assert.Equal([("a specific message", EventLogEntryType.Error)], sink.Entries);
    }

    [Fact]
    public void WriteError_WithoutAMessage_WritesTheExceptionTypeAndMessage()
    {
        var sink = new RecordingSink();

        new ServiceEventLogWriter(sink).WriteError(new InvalidOperationException("boom"));

        Assert.Equal([("InvalidOperationException: boom", EventLogEntryType.Error)], sink.Entries);
    }

    [Fact]
    public void WriteInformation_WritesTheMessageAsInformation()
    {
        var sink = new RecordingSink();

        new ServiceEventLogWriter(sink).WriteInformation("DbDataSync started — console at https://localhost:5001");

        Assert.Equal([("DbDataSync started — console at https://localhost:5001", EventLogEntryType.Information)], sink.Entries);
    }

    [Fact]
    public void Fail_UnderAWindowsService_GoesToTheEventLogAndNotTheConsole()
    {
        var originalErr = Console.Error;
        using var console = new StringWriter();
        Console.SetError(console);
        try
        {
            var logged = new List<(Exception Exception, string? Message)>();
            var exception = new InvalidOperationException("boom");

            var exitCode = ServeCommand.Fail(exception, "a specific message", () => true, (ex, text) => logged.Add((ex, text)));

            Assert.Equal(1, exitCode);
            Assert.Equal([(exception, (string?)"a specific message")], logged);
            Assert.Equal("", console.ToString());
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }

    [Fact]
    public void Fail_NotUnderAWindowsService_GoesToTheConsoleAndNotTheEventLog()
    {
        var originalErr = Console.Error;
        using var console = new StringWriter();
        Console.SetError(console);
        try
        {
            var logged = false;

            var exitCode = ServeCommand.Fail(new InvalidOperationException("boom"), null, () => false, (_, _) => logged = true);

            Assert.Equal(1, exitCode);
            Assert.False(logged);
            Assert.Equal($"InvalidOperationException: boom{Environment.NewLine}", console.ToString());
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }
}
