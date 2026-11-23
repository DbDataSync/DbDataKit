using System.Diagnostics;
using System.Text;
using DbDataKit.Hosting.Startup;
using DbDataKit.Hosting.Systemd;

namespace DbDataKit.Hosting.Cli;

/// <summary>What a program printed (standard output and error together) and its exit code.</summary>
public sealed record CommandResult(int ExitCode, string Output);

/// <summary>Runs <c>systemctl</c>, <c>useradd</c>, <c>chown</c> and the like; tests replace it.</summary>
public delegate CommandResult RunCommand(string program, IReadOnlyList<string> arguments);

/// <summary>
/// systemd for <c>setup single-server</c>: each service runs as a system user of its own, named as the program, in the group
/// <see cref="SuiteServices.LinuxGroup"/>, from the unit its own <c>service unit</c> prints (the one <c>install.sh --system</c> writes). The service's data
/// folder is made for that user (mode 0700) before its first start, so the join request can be put there, and the program folder is root's with no
/// write for the group or others.
/// Design: docs/install.md#setting-up-one-machine-or-inviting-another
/// </summary>
/// <param name="run">Runs a program to its end; <see cref="Run"/> by default.</param>
/// <param name="unitFolder">Where the units are written: <c>/etc/systemd/system</c>.</param>
/// <param name="isRoot">Whether the command runs as root.</param>
public sealed class SystemdSuiteServiceHost(RunCommand? run = null, string unitFolder = "/etc/systemd/system", Func<bool>? isRoot = null) : ISuiteServiceHost
{
    private readonly RunCommand _run = run ?? Run;

    public bool IsElevated => (isRoot ?? (() => Environment.IsPrivilegedProcess))();

    /// <summary>The unit file of <paramref name="service"/>.</summary>
    public string UnitPath(SuiteService service) => Path.Combine(unitFolder, service.Program + ".service");

    public RegistrationCheck Check(SuiteRegistration registration)
    {
        var path = UnitPath(registration.Service);
        if (!File.Exists(path)) return new(RegistrationState.NotRegistered);
        var text = File.ReadAllText(path);
        var registered = text.ReplaceLineEndings("\n").Split('\n').FirstOrDefault(l => l.StartsWith("ExecStart=", StringComparison.Ordinal))?["ExecStart=".Length..];
        var same = text == SystemdUnitGenerator.Generate(registration.Unit)
                   && Ok("id", ["-u", registration.Service.LinuxUser])
                   && Ok("systemctl", ["is-enabled", "--quiet", registration.Service.Program]);
        return new(same ? RegistrationState.Same : RegistrationState.Different, registered);
    }

    public void Register(SuiteRegistration registration)
    {
        var service = registration.Service;
        var group = SuiteServices.LinuxGroup;
        // the same user and group install.sh --system makes: a system account with no home and no login shell
        if (!Ok("getent", ["group", group])) Must("groupadd", ["--system", group]);
        if (!Ok("id", ["-u", service.LinuxUser]))
        {
            var nologin = new[] { "/usr/sbin/nologin", "/sbin/nologin" }.FirstOrDefault(File.Exists) ?? "/bin/false";
            Must("useradd", ["--system", "--gid", group, "--no-create-home", "--home-dir", "/nonexistent", "--shell", nologin, service.LinuxUser]);
        }
        var path = UnitPath(service);
        var text = SystemdUnitGenerator.Generate(registration.Unit);
        if (!File.Exists(path) || File.ReadAllText(path) != text)
        {
            Directory.CreateDirectory(unitFolder);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        Must("systemctl", ["daemon-reload"]);
        // started at boot, as the Windows service is
        Must("systemctl", ["enable", "--quiet", service.Program]);
    }

    public void PrepareFolders(SuiteRegistration registration)
    {
        var service = registration.Service;
        var data = registration.Member.DataFolder;
        // what systemd's StateDirectory= makes at the service's start, made now so the join request can be written before it: the service's user owns
        // the folder and no other account can open it
        Directory.CreateDirectory(data);
        Must("chown", [$"{service.LinuxUser}:{SuiteServices.LinuxGroup}", data]);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(data, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // the program folder is root's, and nobody else can write to it or to anything in it
        var programs = registration.Member.ProgramFolder;
        Must("chown", ["-R", "root:root", programs]);
        Must("chmod", ["-R", "go-w", programs]);
    }

    public void GiveToService(SuiteRegistration registration, string file) =>
        Must("chown", [$"{registration.Service.LinuxUser}:{SuiteServices.LinuxGroup}", file]);

    public SuiteServiceState State(SuiteService service)
    {
        if (!File.Exists(UnitPath(service))) return SuiteServiceState.NotRegistered;
        var state = _run("systemctl", ["show", "--property=ActiveState", "--value", service.Program]).Output.Trim();
        return state switch
        {
            "active" => SuiteServiceState.Running,
            "failed" => SuiteServiceState.Failed,
            "inactive" => SuiteServiceState.Stopped,
            _ => SuiteServiceState.Starting,
        };
    }

    // a unit of type notify would make a blocking start wait for the service's whole start; setup watches the start itself
    public void Start(SuiteService service) => Must("systemctl", ["start", "--no-block", service.Program]);

    public Task StopAsync(SuiteService service, TimeSpan timeout)
    {
        Must("systemctl", ["stop", service.Program]);
        return Task.CompletedTask;
    }

    private bool Ok(string program, IReadOnlyList<string> arguments) => _run(program, arguments).ExitCode == 0;

    private void Must(string program, IReadOnlyList<string> arguments)
    {
        var result = _run(program, arguments);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"`{program} {string.Join(' ', arguments)}` exited {result.ExitCode}: {result.Output.Trim()}");
    }

    /// <summary>Runs a program and waits up to two minutes for it.</summary>
    public static CommandResult Run(string program, IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException($"{program} did not start.");
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(120_000))
            {
                process.Kill(entireProcessTree: true);
                return new CommandResult(-1, $"{program} did not finish in two minutes.");
            }
            return new CommandResult(process.ExitCode, output + error.Result);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new CommandResult(-1, $"{program} could not be run: {ex.Message}");
        }
    }
}
