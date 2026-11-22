using System.Diagnostics;

namespace DbDataKit.Hosting.Startup.Tests;

/// <summary>The start of a server in a process of its own: the exit code the service manager sees, and what a killed process leaves.</summary>
public sealed class StartupProcessTests : StartupTestBase
{
    private static string ChildDll => Path.Combine(AppContext.BaseDirectory, "DbDataKit.Hosting.Startup.TestChild.dll");

    private static string Dotnet => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";

    private Process Start(params string[] args)
    {
        var info = new ProcessStartInfo(Dotnet) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { ChildDll, Logs, Data }.Concat(args)) info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }

    [Fact]
    public async Task A_process_whose_step_fails_exits_non_zero_and_leaves_the_reason()
    {
        using var child = Start("fail", "state");
        await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(StartupSequence.FailureExitCode, child.ExitCode);
        Assert.NotEqual(0, child.ExitCode);
        var text = Read(FailureFile);
        Assert.Contains("Step: state", text);
        Assert.Contains("the state step of the test child fails", text);
    }

    [Fact]
    public async Task A_process_that_starts_cleanly_exits_zero_and_leaves_no_file()
    {
        using var child = Start("ok");
        await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(0, child.ExitCode);
        Assert.False(File.Exists(FailureFile));
    }

    [Fact]
    public async Task A_process_killed_in_a_step_leaves_that_step_not_finished()
    {
        var marker = Path.Combine(Root, "in-step");
        using var child = Start("hang", "certificate", marker);
        try
        {
            var waited = Stopwatch.StartNew();
            while (!File.Exists(marker) && !child.HasExited && waited.Elapsed < TimeSpan.FromSeconds(60))
                await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(marker), "the child never reached the certificate step: " + (child.HasExited ? await child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken) : "still running"));
        }
        finally
        {
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        var text = Read(FailureFile).Trim();
        Assert.Matches("^starting since .+Z, step: certificate, not finished$", text);
    }
}
