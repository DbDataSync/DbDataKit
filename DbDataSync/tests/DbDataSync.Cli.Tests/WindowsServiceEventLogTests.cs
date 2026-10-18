using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// The one part of the Windows Event Log integration that is ours and needs a real Windows machine: that <c>service install</c>
/// leaves the <c>DbDataSync</c> source registered against the Application log. What gets written, and when, is tested against a fake
/// in <c>ServiceEventLogWriterTests</c>.
/// <para>
/// There used to be two tests here that wrote an entry and read it back out of the machine's live Application log. They tested
/// Windows' event log service, not this product, and failed on timing the product does not control (a just-registered source is
/// briefly unreadable). They were removed rather than made to wait; if an end-to-end check of the written entry is wanted, it
/// belongs in a nightly Windows lane, not on the gate that promotes a commit.
/// </para>
/// </summary>
public sealed class WindowsServiceEventLogTests
{
    /// <summary>
    /// Registering an already-registered source must not throw — <c>service install</c> has to tolerate being re-run (upgrade,
    /// repair). It also has to leave the source registered against the Application log: a silently no-op'd registration, or a source
    /// bound to the wrong log, would break every write for real.
    /// </summary>
    [WindowsOnlyFact]
    [SupportedOSPlatform("windows")]
    public void EnsureSourceRegistered_CalledTwice_LeavesTheSourceBoundToTheApplicationLog()
    {
        EnsureSourceRegisteredOrExplain();
        EnsureSourceRegisteredOrExplain();

        var source = WindowsServiceEventLog.SourceName;
        var exists = EventLog.SourceExists(source);
        var logName = exists ? EventLog.LogNameFromSourceName(source, ".") : "(source does not exist)";

        Assert.True(exists, $"EventLog.SourceExists(\"{source}\") is false after two EnsureSourceRegistered() calls.");
        Assert.Equal("Application", logName);
    }

    /// <summary>
    /// <c>EventLog.SourceExists</c> searches every log, including Security, so it throws <see cref="SecurityException"/> when this
    /// process is not elevated. CI's windows-latest runners are elevated; say so on a developer's machine instead of a bare stack trace.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void EnsureSourceRegisteredOrExplain()
    {
        try
        {
            WindowsServiceEventLog.EnsureSourceRegistered();
        }
        catch (SecurityException ex)
        {
            Assert.Fail(
                $"Could not register or check the Event Log source '{WindowsServiceEventLog.SourceName}'. "
                + $"This process is {(WindowsElevation.IsAdministrator() ? "elevated" : "NOT elevated")}, and "
                + "EventLog.SourceExists must search every log (including Security) to answer, which requires "
                + $"Administrator. Run this suite from an elevated prompt. The underlying error was: {ex.Message}");
        }
    }
}
