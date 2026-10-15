using System.Diagnostics;
using System.Security.Cryptography;

namespace DbDataKit.Hosting.Children;

/// <summary>What the host tells the child, through environment variables only (never argv).</summary>
public sealed record ChildLaunchInfo(string Token, ChildEndpoint Endpoint, int ParentPid, long ParentStartTicks);

public static class ChildEnvironment
{
    public const string TokenVariable = "DBDATA_CHILD_TOKEN";
    public const string EndpointVariable = "DBDATA_CHILD_ENDPOINT";
    public const string ParentPidVariable = "DBDATA_PARENT_PID";
    public const string ParentStartVariable = "DBDATA_PARENT_START";

    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Child side: read the launch info, or null when not started by a <see cref="ChildProcessHost"/>.</summary>
    public static ChildLaunchInfo? TryRead(Func<string, string?>? getVariable = null)
    {
        getVariable ??= Environment.GetEnvironmentVariable;
        var token = getVariable(TokenVariable);
        var endpoint = getVariable(EndpointVariable);
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(endpoint)) return null;
        _ = int.TryParse(getVariable(ParentPidVariable), out var pid);
        _ = long.TryParse(getVariable(ParentStartVariable), out var start);
        return new ChildLaunchInfo(token, ChildEndpoint.Parse(endpoint), pid, start);
    }

    internal static void Apply(IDictionary<string, string?> environment, ChildLaunchInfo info)
    {
        environment[TokenVariable] = info.Token;
        environment[EndpointVariable] = info.Endpoint.ToString();
        environment[ParentPidVariable] = info.ParentPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        environment[ParentStartVariable] = info.ParentStartTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static long StartTicksOf(Process p)
    {
        try { return p.StartTime.ToUniversalTime().Ticks; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return 0; }
    }
}

/// <summary>
/// Child-side guard: invokes a callback when the parent process is gone, so a child never outlives its host even
/// where no job object exists (Linux) or the job was bypassed. Guards against pid reuse using the parent's start time.
/// </summary>
public static class ParentDeathMonitor
{
    public static Task Start(int parentPid, long parentStartTicks, Action onParentExit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onParentExit);
        if (parentPid <= 0) return Task.CompletedTask;

        return Task.Run(async () =>
        {
            Process parent;
            try
            {
                parent = Process.GetProcessById(parentPid);
                if (parentStartTicks != 0 && ChildEnvironment.StartTicksOf(parent) != parentStartTicks)
                {
                    onParentExit(); // pid was reused: our real parent is already gone
                    return;
                }
            }
            catch (ArgumentException)
            {
                onParentExit();
                return;
            }

            using (parent)
            {
                try
                {
                    await parent.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            onParentExit();
        }, CancellationToken.None);
    }

    public static Task Start(ChildLaunchInfo info, Action onParentExit, CancellationToken cancellationToken = default) =>
        Start(info.ParentPid, info.ParentStartTicks, onParentExit, cancellationToken);
}
