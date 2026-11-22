using System.Diagnostics;
using DbDataKit.Hosting.WindowsService;

namespace DbDataKit.Hosting.Cli;

/// <summary>
/// Restarts a suite service that is registered on this machine, for a command that changed what the service reads at its start (<c>pair</c>): on
/// Windows through the service control manager, on Linux with <c>systemctl restart</c> when the service's unit is installed. The service is started and
/// not waited for, since a start that fails is reported in its startup-failure file.
/// </summary>
public static class ServiceRestarter
{
    /// <summary>Where systemd keeps the units an install writes.</summary>
    public static readonly string[] UnitFolders = ["/etc/systemd/system", "/lib/systemd/system", "/usr/lib/systemd/system"];

    /// <param name="windowsName">The Windows service's name (<c>DbDataRunner</c>).</param>
    /// <param name="unit">The systemd unit's name without <c>.service</c> (<c>dbdatarunner</c>).</param>
    /// <param name="scm">The service control manager; the machine's on Windows.</param>
    /// <param name="timeout">How long stopping the Windows service may take.</param>
    public static async Task<(bool Registered, string? Problem)> RestartAsync(string windowsName, string unit, IServiceControlManager? scm = null, TimeSpan? timeout = null)
    {
        if (OperatingSystem.IsWindows())
        {
            scm ??= new WindowsServiceControlManager();
            try
            {
                if (scm.GetConfig(windowsName) is null) return (false, null);
                await new ServiceProvisioner(scm, null).StopAsync(windowsName, timeout ?? TimeSpan.FromSeconds(60)).ConfigureAwait(false);
                scm.Start(windowsName);
                return (true, null);
            }
            catch (ServiceControlException ex) when (ex.Error == ServiceControlError.AccessDenied) { return (true, ElevationDetector.Message("restarting the service")); }
            catch (Exception ex) when (ex is ServiceControlException or TimeoutException) { return (true, ex.Message); }
        }

        if (!OperatingSystem.IsLinux() || !UnitFolders.Any(f => File.Exists(Path.Combine(f, unit + ".service")))) return (false, null);
        try
        {
            using var systemctl = Process.Start(new ProcessStartInfo("systemctl", ["restart", "--no-block", unit]) { UseShellExecute = false, RedirectStandardError = true })!;
            var error = await systemctl.StandardError.ReadToEndAsync().ConfigureAwait(false);
            await systemctl.WaitForExitAsync().ConfigureAwait(false);
            return (true, systemctl.ExitCode == 0 ? null : $"systemctl restart {unit} exited {systemctl.ExitCode}: {error.Trim()}");
        }
        catch (System.ComponentModel.Win32Exception ex) { return (true, $"systemctl could not be run: {ex.Message}"); }
    }
}
