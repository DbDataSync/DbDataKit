using System.Diagnostics;

namespace DbDataProxy.Server.Hosting;

/// <summary>Registers or removes the Windows service with the Service Control Manager (needs an elevated prompt).</summary>
public static class ServiceInstaller
{
    public static int Install(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return Fail("Service installation is only available on Windows. Use systemd or Docker elsewhere.");

        string name = Option(args, "--name") ?? ProxyHost.ServiceName;
        string? account = Option(args, "--account");
        string? password = Option(args, "--password");
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the executable path.");

        var create = new List<string>
        {
            "create", name,
            "binPath=", $"\"{exe}\"",
            "start=", "auto",
            "DisplayName=", "DbDataProxy Arrow Flight SQL proxy",
        };
        if (account is not null)
        {
            create.Add("obj="); create.Add(account);
            if (password is not null) { create.Add("password="); create.Add(password); }
        }

        int rc = Sc(create);
        if (rc != 0) return rc;
        Sc(["description", name, "Arrow Flight SQL proxy that forwards queries to ODBC/ADO.NET databases and compresses the results."]);
        // Restart automatically after a crash: after 5s, 5s, then 60s; reset the failure count after a day.
        Sc(["failure", name, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/60000"]);

        Console.WriteLine($"Service '{name}' installed. Edit appsettings.json beside {exe}, then run: sc start {name}");
        Console.WriteLine("Open the listening port in the Windows firewall if clients connect from other machines.");
        return 0;
    }

    public static int Uninstall(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return Fail("Service removal is only available on Windows.");
        string name = Option(args, "--name") ?? ProxyHost.ServiceName;
        Sc(["stop", name]);
        return Sc(["delete", name]);
    }

    private static int Sc(IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (!string.IsNullOrWhiteSpace(output)) Console.WriteLine(output.Trim());
        return process.ExitCode;
    }

    private static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}
