using System.Diagnostics;

namespace DbDataKit.Apps.Bundling;

/// <summary>How hard a build is kept off the network.</summary>
public enum NetworkIsolation
{
    /// <summary>No operating-system isolation. Nothing is installed and no proxy variable is passed (the build's environment is cleared), but the program could still open a socket.</summary>
    None,
    /// <summary>The build runs in a network namespace with no route when the machine can make one (Linux), and as <see cref="None"/> where it cannot; the result's notes say which.</summary>
    IfAvailable,
    /// <summary>The build runs in a network namespace with no route or does not run: a machine that cannot make one refuses the build and says why.</summary>
    Required,
}

/// <summary>
/// Runs a child process in a network namespace of its own (Linux: <c>unshare --net</c>, from util-linux): a namespace has no interface but a loopback that is down and no route, so the child cannot connect anywhere, to the machine's
/// other services (a database on 127.0.0.1) included. This is the one operating-system guarantee of the app build; see <c>docs/apps.md</c> (what it guarantees and what it does not).
/// </summary>
public static class NetworkIsolator
{
    private static readonly Lazy<IReadOnlyList<string>?> Prefix = new(Probe);

    /// <summary>True when this machine can start a process with no network.</summary>
    public static bool IsAvailable => Prefix.Value is not null;

    /// <summary>The program and arguments that run <paramref name="program"/> with no network, or null when this machine cannot.</summary>
    public static (string FileName, IReadOnlyList<string> Arguments)? Wrap(string program, IReadOnlyList<string> arguments)
    {
        if (Prefix.Value is not { } prefix) return null;
        return (prefix[0], [.. prefix.Skip(1), program, .. arguments]);
    }

    /// <summary>Why a machine cannot isolate (for a refusal's message).</summary>
    public static string WhyNot => OperatingSystem.IsLinux()
        ? "this Linux machine could not make a network namespace (`unshare --net` failed: it needs util-linux's unshare and either root or user namespaces)"
        : "network namespaces exist on Linux only";

    private static IReadOnlyList<string>? Probe()
    {
        if (!OperatingSystem.IsLinux()) return null;
        // as root (or with the capability) a plain namespace; otherwise a user namespace that maps the caller to root inside it
        foreach (var prefix in new[] { new[] { "unshare", "--net", "--" }, ["unshare", "--user", "--map-root-user", "--net", "--"] })
            if (Works(prefix)) return prefix;
        return null;
    }

    private static bool Works(string[] prefix)
    {
        try
        {
            var info = new ProcessStartInfo(prefix[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in prefix.Skip(1)) info.ArgumentList.Add(a);
            info.ArgumentList.Add("true");
            using var process = Process.Start(info)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            return process.WaitForExit(10_000) && process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
