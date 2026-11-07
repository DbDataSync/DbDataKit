using System.Diagnostics;
using System.Text;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Limits;
using DbDataKit.State;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Runner.Execution.Tests;

/// <summary>Plan 28a R06: real task processes, started by the executor through the task host (or as executables), against a runner protocol host and a state database on each engine.</summary>
public sealed class TaskExecutorTests
{
    public static TheoryData<string> Engines => ExecutionEngines.Engines;

    private static StoredRun Stored(ExecutorHarness h, Guid runId) => h.Runs.Get(runId)!;

    // ---- the outcomes

    [Theory, MemberData(nameof(Engines))]
    public async Task A_task_that_succeeds_ends_succeeded_with_its_output_in_the_log_and_the_run_recorded(string engine)
    {
        using var h = new ExecutorHarness(engine);
        var request = await h.SampleRequest("ok");

        var result = await h.Executor.RunAsync(request);

        Assert.True(result.End.State == TaskEndState.Succeeded, Why(h, request, result));
        Assert.True(result.EndReceived);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.KilledByExecutor);
        Assert.Equal(Scheduling.WorkItemStatus.Done, result.WorkItemStatus);
        var run = Stored(h, request.RunId!.Value);
        Assert.Equal(RunOutcome.Succeeded, run.Run.Outcome);
        Assert.Equal(("sample", "1.0.0", "prod", 1), (run.Package, run.PackageVersion, run.Channel, run.Attempt));
        Assert.Equal("runner", run.Service);
        Assert.Equal("sample/prod/ok", run.Run.JobId);
        var log = h.Log(request.RunId!.Value);
        Assert.Contains(log, l => l is { Level: "Information" } && l.Message == "hello from ok");
        Assert.Contains(log, l => l is { Level: "Warning" } && l.Message == "a warning on standard error");
        Assert.True(result.StdoutLines >= 1 && result.StderrLines == 1);
        Assert.NotEqual(LimitEnforcement.None, result.MemoryLimit); // the default memory limit is applied and says how it is held
        Assert.NotEqual("none", result.LimitMechanism);
        Assert.False(Directory.Exists(Path.Combine(h.ScratchRoot, request.RunId!.Value.ToString("N"))));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_task_that_fails_ends_failed_with_a_classified_failure_and_the_exit_code_zero(string engine)
    {
        using var h = new ExecutorHarness(engine);
        var request = await h.SampleRequest("fails");

        var result = await h.Executor.RunAsync(request);

        Assert.Equal(TaskEndState.Failed, result.End.State);
        Assert.True(result.EndReceived);
        Assert.Equal(TaskFailureKind.Permanent, result.End.Failure!.Kind);
        Assert.Contains("the job broke", result.End.Failure.Message);
        Assert.Equal(0, result.ExitCode); // the host got as far as sending the end message
        var run = Stored(h, request.RunId!.Value);
        Assert.Equal(RunOutcome.Failed, run.Run.Outcome);
        Assert.Equal("Permanent", run.Run.Failure?.Kind);
        Assert.Equal(Scheduling.WorkItemStatus.Failed, result.WorkItemStatus);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_cancelled_task_that_stops_within_the_grace_period_is_not_killed(string engine)
    {
        using var h = new ExecutorHarness(engine);
        var request = await h.SampleRequest("waits", s => s.Grace = TimeSpan.FromSeconds(20));
        using var cancel = new CancellationTokenSource();
        var run = h.Executor.RunAsync(request, cancel.Token);
        await h.WaitForLog(request.RunId!.Value, "ready");

        var stopwatch = Stopwatch.StartNew();
        await cancel.CancelAsync();
        var result = await run;

        Assert.Equal(TaskEndState.Stopped, result.End.State);
        Assert.Equal(TaskFailureKind.Cancelled, result.End.Failure!.Kind);
        Assert.True(result.EndReceived);
        Assert.False(result.KilledByExecutor);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"the task stopped on request, it took {stopwatch.Elapsed}");
        Assert.Contains(h.Log(request.RunId.Value), l => l.Message == "stopping on request");
        Assert.Equal(RunOutcome.Stopped, Stored(h, request.RunId.Value).Run.Outcome);
        Assert.Equal(Scheduling.WorkItemStatus.Cancelled, result.WorkItemStatus);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_cancelled_task_that_ignores_the_request_is_killed_after_the_grace_period(string engine)
    {
        using var h = new ExecutorHarness(engine);
        var grace = TimeSpan.FromSeconds(2);
        var request = await h.SampleRequest("stubborn", s => s.Grace = grace);
        using var cancel = new CancellationTokenSource();
        var run = h.Executor.RunAsync(request, cancel.Token);
        await h.WaitForLog(request.RunId!.Value, "ready");
        var pid = await h.PidOf(request.RunId.Value);

        var stopwatch = Stopwatch.StartNew();
        await cancel.CancelAsync();
        var result = await run;

        Assert.True(stopwatch.Elapsed >= grace - TimeSpan.FromMilliseconds(100), $"the grace period was honoured, the run took {stopwatch.Elapsed} after the cancel");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30));
        Assert.Equal(TaskEndState.Stopped, result.End.State);
        Assert.Equal(TaskFailureKind.Cancelled, result.End.Failure!.Kind);
        Assert.True(result.KilledByExecutor);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(RunOutcome.Stopped, Stored(h, request.RunId.Value).Run.Outcome);
        Assert.False(IsAlive(pid), "the process tree was killed");
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_task_that_runs_past_its_time_limit_is_killed_and_ends_timed_out(string engine)
    {
        using var h = new ExecutorHarness(engine);
        var request = await h.SampleRequest("stubborn", s => s.Limits = new TaskLimits(Timeout: TimeSpan.FromSeconds(3)));

        var result = await h.Executor.RunAsync(request);

        Assert.Equal(TaskEndState.Failed, result.End.State);
        Assert.Equal(TaskFailureKind.TimedOut, result.End.Failure!.Kind);
        Assert.False(result.EndReceived);
        Assert.True(result.KilledByExecutor);
        Assert.True(result.Duration >= TimeSpan.FromSeconds(3));
        Assert.True(result.End.Failure.Retryable);
        var run = Stored(h, request.RunId!.Value);
        Assert.Equal(RunOutcome.Failed, run.Run.Outcome);
        Assert.Equal("TimedOut", run.Run.Failure?.Kind);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_process_that_exits_with_zero_and_no_end_message_is_a_protocol_violation(string engine)
    {
        using var h = new ExecutorHarness(engine);
        var request = await h.SampleRequest("exits-zero");

        var result = await h.Executor.RunAsync(request);

        Assert.Equal(TaskEndState.Failed, result.End.State);
        Assert.Equal(TaskFailureKind.ProtocolViolation, result.End.Failure!.Kind);
        Assert.False(result.EndReceived);
        Assert.Equal(0, result.ExitCode);
        Assert.False(result.KilledByExecutor);
        Assert.Equal("ProtocolViolation", Stored(h, request.RunId!.Value).Run.Failure?.Kind);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_process_that_exits_with_a_nonzero_code_and_no_end_message_is_process_exited(string engine)
    {
        using var h = new ExecutorHarness(engine);
        var request = await h.SampleRequest("exits-three");

        var result = await h.Executor.RunAsync(request);

        Assert.Equal(TaskEndState.Failed, result.End.State);
        Assert.Equal(TaskFailureKind.ProcessExited, result.End.Failure!.Kind);
        Assert.Contains("code 3", result.End.Failure.Message);
        Assert.Equal(3, result.ExitCode);
        Assert.Equal("ProcessExited", Stored(h, request.RunId!.Value).Run.Failure?.Kind);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_process_that_crashes_after_its_end_message_keeps_the_outcome_it_reported(string engine)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the test kills itself with SIGKILL semantics; on Windows the exit code differs");
        using var h = new ExecutorHarness(engine);
        var request = await h.SampleRequest("crashes-after-end");

        var result = await h.Executor.RunAsync(request);

        Assert.True(result.End.State == TaskEndState.Succeeded, Why(h, request, result));
        Assert.True(result.EndReceived);
        Assert.NotEqual(0, result.ExitCode); // it did crash
        Assert.False(result.KilledByExecutor);
        Assert.Equal(RunOutcome.Succeeded, Stored(h, request.RunId!.Value).Run.Outcome);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_task_over_its_memory_limit_is_killed_by_the_watchdog_and_ends_limit_exceeded(string engine)
    {
        using var h = new ExecutorHarness(engine, s => s.Limits = new LimitOptions { DisableCgroup = true, DisableJobObject = true, SampleInterval = TimeSpan.FromMilliseconds(40) });
        var request = await h.SampleRequest("allocates", s =>
        {
            s.Limits = new TaskLimits(MemoryMb: 400);
            s.Inputs = new Dictionary<string, string> { ["megabytes"] = "3000" };
        });

        var result = await h.Executor.RunAsync(request);

        Assert.Equal(TaskEndState.Failed, result.End.State);
        Assert.Equal(TaskFailureKind.LimitExceeded, result.End.Failure!.Kind);
        Assert.False(result.EndReceived);
        Assert.Equal(LimitEnforcement.Watched, result.MemoryLimit);
        Assert.Equal("watchdog", result.LimitMechanism);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("LimitExceeded", Stored(h, request.RunId!.Value).Run.Failure?.Kind);
        Assert.Contains(h.Log(request.RunId.Value), l => l.Message.Contains("resident memory", StringComparison.Ordinal));
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_secret_a_task_prints_is_redacted_in_the_log_and_the_raw_value_is_nowhere(string engine)
    {
        using var h = new ExecutorHarness(engine);
        const string secret = "s3cr3t-Value-4711";
        h.Redactor.Register(secret);
        var request = await h.SampleRequest("prints-secret", s => s.Inputs = new Dictionary<string, string> { ["secret"] = secret });

        var result = await h.Executor.RunAsync(request);

        Assert.True(result.End.State == TaskEndState.Succeeded, Why(h, request, result));
        var log = h.Log(request.RunId!.Value);
        Assert.Contains(log, l => l.Message == "the password is ***");
        Assert.Contains(log, l => l.Message.Contains("\"token\":\"***\"", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Message.Contains("Password=***", StringComparison.Ordinal));
        Assert.Contains(log, l => l.Message == "and a harmless line");
        foreach (var line in log)
        {
            Assert.DoesNotContain(secret, line.Message);
            Assert.DoesNotContain(secret, line.Level);
            Assert.DoesNotContain(secret, line.Item ?? "");
        }

        var run = Stored(h, request.RunId.Value).Run;
        Assert.DoesNotContain(secret, run.Failure?.Message ?? "");
    }

    // ---- what a task can reach

    [Theory, MemberData(nameof(Engines))]
    public async Task The_token_and_endpoint_are_in_the_environment_only_never_in_argv_and_on_no_disk(string engine)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "reads /proc");
        using var h = new ExecutorHarness(engine);
        Environment.SetEnvironmentVariable("RUNNER_STATE_PASSWORD", "state-password-of-the-runner");
        try
        {
            var version = await PackageFixture.SamplePackage();
            var request = await h.SampleRequest("probe");
            using var cancel = new CancellationTokenSource();
            var run = h.Executor.RunAsync(request, cancel.Token);
            await h.WaitForLog(request.RunId!.Value, "ready");
            var pid = await h.PidOf(request.RunId.Value);

            var environ = (await File.ReadAllTextAsync($"/proc/{pid}/environ")).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Split('=', 2)).ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");
            var cmdline = await File.ReadAllTextAsync($"/proc/{pid}/cmdline");

            // by design the token and the endpoint are in the environment
            var token = environ["DBDATAKIT_RUNNER_TOKEN"];
            var endpoint = environ["DBDATAKIT_RUNNER_ENDPOINT"];
            Assert.StartsWith("unix:", endpoint);
            Assert.True(token.Length >= 32);
            // and nowhere else
            Assert.DoesNotContain(token, cmdline);
            Assert.DoesNotContain(endpoint, cmdline);
            Assert.DoesNotContain(endpoint["unix:".Length..], cmdline);
            var scratch = Path.Combine(h.ScratchRoot, request.RunId.Value.ToString("N"));
            Assert.True(Directory.Exists(scratch));
            var scanned = 0;
            foreach (var root in new[] { scratch, version.Directory })
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    // the runtime makes pipes and sockets for its debugger and diagnostics in the temp directory; opening a pipe for reading waits for a writer, forever
                    var name = Path.GetFileName(file);
                    if (name.StartsWith("clr-debug-pipe", StringComparison.Ordinal) || name.StartsWith("dotnet-diagnostic", StringComparison.Ordinal)) continue;
                    byte[] bytes;
                    try
                    {
                        bytes = await File.ReadAllBytesAsync(file);
                    }
                    catch (IOException)
                    {
                        continue; // a socket (the runtime's diagnostic one) holds no file content
                    }

                    Assert.False(Contains(bytes, token), $"the token is in {file}");
                    Assert.False(Contains(bytes, endpoint), $"the endpoint is in {file}");
                    scanned++;
                }

            Assert.True(scanned > 10);
            // the runner's own environment is not the task's
            Assert.DoesNotContain("RUNNER_STATE_PASSWORD", environ.Keys);
            var log = h.Log(request.RunId.Value);
            Assert.Contains(log, l => l.Message == "STATE_VAR=unset");
            Assert.Contains(log, l => l.Message == $"CWD={scratch}");
            Assert.Contains(log, l => l.Message == $"SCRATCH={scratch}");
            Assert.Contains(log, l => l.Message.StartsWith($"TMP={scratch}", StringComparison.Ordinal));
            Assert.Contains(log, l => l.Message == $"PACKAGE={version.Directory}");

            await cancel.CancelAsync();
            var result = await run;
            Assert.Equal(TaskEndState.Stopped, result.End.State);
            Assert.False(Directory.Exists(scratch), "the scratch directory is removed after the run");
            foreach (var line in h.Log(request.RunId.Value))
            {
                Assert.DoesNotContain(token, line.Message);
                Assert.DoesNotContain(endpoint, line.Message);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("RUNNER_STATE_PASSWORD", null);
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_materialized_directory_is_read_only_to_the_job(string engine)
    {
        using var h = new ExecutorHarness(engine);
        var version = await PackageFixture.SamplePackage();
        Assert.SkipWhen(OperatingSystem.IsWindows(), "on Windows the read-only attribute of a directory does not stop a file being created in it (a known limit of the artifact cache, plan 28a R05); only existing files are protected");
        Assert.SkipWhen(ModeBitsDoNotBindThisUser(), "this user can write anything (root); run the suite as an ordinary user to see the job refused");
        var request = await h.SampleRequest("writes-package");

        var result = await h.Executor.RunAsync(request);

        Assert.True(result.End.State == TaskEndState.Succeeded, result.End.Failure?.Message);
        Assert.Contains(h.Log(request.RunId!.Value), l => l.Message.StartsWith("refused: create a file", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(version.Directory, "intruder.txt")));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task The_view_a_job_is_handed_is_the_caches_read_only_view()
    {
        // always asserted, also for root, who the mode bits do not stop: the directory the job gets is the cache's view (0555 for directories, 0444 for objects)
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes");
        var version = await PackageFixture.SamplePackage();
        const UnixFileMode write = UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(version.Directory) & write);
        var dll = Path.Combine(version.Directory, "DbDataKit.Runner.TestPackage.dll");
        var target = File.ResolveLinkTarget(dll, returnFinalTarget: true);
        Assert.Equal(UnixFileMode.None, File.GetUnixFileMode(target?.FullName ?? dll) & write);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task The_scratch_directory_is_removed_after_the_run_also_when_the_job_made_things_read_only(string engine)
    {
        using var h = new ExecutorHarness(engine);
        var request = await h.SampleRequest("fills-scratch");

        var result = await h.Executor.RunAsync(request);

        Assert.True(result.End.State == TaskEndState.Succeeded, Why(h, request, result));
        Assert.True(result.ScratchBytes >= 3 * 1024 * 1024);
        Assert.False(result.ScratchOverLimit);
        Assert.Empty(Directory.Exists(h.ScratchRoot) ? Directory.EnumerateFileSystemEntries(h.ScratchRoot) : []);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_scratch_directory_over_its_limit_is_a_warning_in_the_log_and_the_result_not_a_failure(string engine)
    {
        using var h = new ExecutorHarness(engine, s => s.MaxScratchBytes = 1024 * 1024);
        var request = await h.SampleRequest("fills-scratch");

        var result = await h.Executor.RunAsync(request);

        Assert.True(result.End.State == TaskEndState.Succeeded, Why(h, request, result));
        Assert.True(result.ScratchOverLimit);
        Assert.Contains(h.Log(request.RunId!.Value), l => l.Level == "Warning" && l.Message.Contains("scratch directory held", StringComparison.Ordinal) && l.Message.Contains("over the limit", StringComparison.Ordinal));
        Assert.Empty(Directory.EnumerateFileSystemEntries(h.ScratchRoot));
    }

    // ---- kinds

    [Theory, MemberData(nameof(Engines))]
    public async Task An_executable_that_speaks_no_protocol_and_exits_zero_ends_in_a_protocol_violation(string engine)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the sample executables are shell scripts");
        using var h = new ExecutorHarness(engine);
        var request = await h.ScriptRequest("exit0.sh");

        var result = await h.Executor.RunAsync(request);

        Assert.Equal(TaskFailureKind.ProtocolViolation, result.End.Failure!.Kind);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(h.Log(request.RunId!.Value), l => l.Message == "a script that speaks no protocol");
        Assert.Equal("scripts", Stored(h, request.RunId.Value).Package);
        Assert.Equal(RunOutcome.Failed, Stored(h, request.RunId.Value).Run.Outcome);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_launch_command_that_is_a_file_of_the_package_starts_although_the_stored_object_was_not_executable(string engine)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the sample executables are shell scripts");
        using var h = new ExecutorHarness(engine);
        var request = await h.ProgramRequest("program.sh");

        var result = await h.Executor.RunAsync(request);

        // the script speaks no protocol, so the run ends in a violation, but it started and ran: 'permission denied' would be a failure to start
        Assert.Equal(TaskFailureKind.ProtocolViolation, result.End.Failure!.Kind);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains(h.Log(request.RunId!.Value), l => l.Message == "started as a program of the package");
    }

    [Fact]
    public async Task An_executable_that_exits_nonzero_is_process_exited_and_gets_its_arguments_and_the_task_name()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the sample executables are shell scripts");
        using var h = new ExecutorHarness(Engine.Sqlite);
        var failing = await h.ScriptRequest("exit3.sh");
        var failed = await h.Executor.RunAsync(failing);
        Assert.Equal(TaskFailureKind.ProcessExited, failed.End.Failure!.Kind);
        Assert.Equal(3, failed.ExitCode);
        Assert.Contains(h.Log(failing.RunId!.Value), l => l is { Level: "Warning" } && l.Message == "failing quietly");

        var withArguments = await h.ScriptRequest("args.sh", "second");
        withArguments = withArguments with { Launch = withArguments.Launch with { Args = ["{package}/args.sh", "first"] }, Entry = new TaskEntry(Args: ["second"]) };
        await h.Executor.RunAsync(withArguments);
        Assert.Contains(h.Log(withArguments.RunId!.Value), l => l.Message == "args: first second");
        Assert.Contains(h.Log(withArguments.RunId.Value), l => l.Message == "task: run");
    }

    [Fact]
    public async Task A_definition_run_by_a_runtime_starts_the_runtimes_command_in_the_definitions_directory_with_both_directories_in_the_environment()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the sample executables are shell scripts");
        using var h = new ExecutorHarness(Engine.Sqlite);
        var runtime = await PackageFixture.ScriptPackage();
        var definition = await h.SampleRequest("anything");
        var request = definition with
        {
            Launch = new PackageLaunch(LaunchKind.Executable, Command: "sh", Args: ["{runtime}/runtime.sh", "first"]),
            RuntimeDirectory = runtime.Directory,
            Entry = new TaskEntry(Args: ["second"]),
            RunId = h.Database.NewId(),
        };

        await h.Executor.RunAsync(request);

        var log = h.Log(request.RunId!.Value);
        Assert.Contains(log, l => l.Message == $"cwd: {Path.GetFullPath(definition.PackageDirectory)}");
        Assert.Contains(log, l => l.Message == $"package: {Path.GetFullPath(definition.PackageDirectory)}");
        Assert.Contains(log, l => l.Message == $"runtime: {Path.GetFullPath(runtime.Directory)}");
        Assert.Contains(log, l => l.Message == "args: first second");
    }

    [Fact]
    public async Task A_runtime_gets_an_extraction_directory_of_its_version_outside_every_view_and_a_package_that_runs_itself_gets_none()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the sample executables are shell scripts");
        using var h = new ExecutorHarness(Engine.Sqlite);
        var runtime = await PackageFixture.ScriptPackage();
        var definition = await h.SampleRequest("anything");
        var request = definition with
        {
            Launch = new PackageLaunch(LaunchKind.Executable, Command: "sh", Args: ["{runtime}/runtime.sh", "first"]),
            RuntimeDirectory = runtime.Directory,
            RuntimeKey = "dbdatabuild-runtime@7",
            RunId = h.Database.NewId(),
        };

        await h.Executor.RunAsync(request);

        // the directory is the runner's, per version, made before the start, and neither the definition's nor the runtime's view
        var expected = Path.Combine(h.DataDirectory, "cache", "runtimes", "dbdatabuild-runtime@7");
        Assert.Contains(h.Log(request.RunId!.Value), l => l.Message == $"extract: {expected}");
        Assert.True(Directory.Exists(expected));
        Assert.DoesNotContain(Path.GetFullPath(runtime.Directory), expected);
        Assert.DoesNotContain(Path.GetFullPath(definition.PackageDirectory), expected);

        // a package that is not run by a runtime has no such variable
        var plain = request with { RuntimeDirectory = null, RuntimeKey = null, Launch = new PackageLaunch(LaunchKind.Executable, Command: "sh", Args: [runtime.Directory + "/runtime.sh"]), RunId = h.Database.NewId() };
        await h.Executor.RunAsync(plain);
        Assert.Contains(h.Log(plain.RunId!.Value), l => l.Message == "extract: ");
    }

    [Fact]
    public async Task A_task_the_package_does_not_have_is_a_host_failure_with_the_reason_in_the_log()
    {
        using var h = new ExecutorHarness(Engine.Sqlite);
        var request = await h.SampleRequest("no-such-task");

        var result = await h.Executor.RunAsync(request);

        Assert.Equal(TaskFailureKind.ProcessExited, result.End.Failure!.Kind);
        Assert.Equal(65, result.ExitCode);
        Assert.Contains(h.Log(request.RunId!.Value), l => l.Message.Contains("no job class with [RunnerTask(\"no-such-task\")]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_task_host_that_cannot_be_found_is_a_failed_run_not_a_crash_and_the_record_is_finished()
    {
        using var h = new ExecutorHarness(Engine.Sqlite);
        var executor = new TaskExecutor(h.Database, h.Host, new TaskExecutorOptions { DataDirectory = h.DataDirectory, TaskHostPath = Path.Combine(h.DataDirectory, "missing.dll") });
        var request = await h.SampleRequest("ok");

        var result = await executor.RunAsync(request);

        Assert.Equal(TaskEndState.Failed, result.End.State);
        Assert.Equal(TaskFailureKind.Permanent, result.End.Failure!.Kind);
        Assert.Null(result.ExitCode);
        Assert.Contains("missing.dll", result.End.Failure.Message);
        Assert.Equal(RunOutcome.Failed, Stored(h, request.RunId!.Value).Run.Outcome);
        Assert.Empty(Directory.EnumerateFileSystemEntries(h.ScratchRoot));
    }

    [Fact]
    public async Task A_request_that_is_wrong_is_refused_before_any_run_is_recorded()
    {
        using var h = new ExecutorHarness(Engine.Sqlite);
        var request = (await h.SampleRequest("ok")) with { PackageDirectory = Path.Combine(h.DataDirectory, "nowhere"), Attempt = 0 };

        var thrown = await Assert.ThrowsAsync<ArgumentException>(() => h.Executor.RunAsync(request));

        Assert.Contains("nowhere", thrown.Message);
        Assert.Contains("attempt number is 0", thrown.Message);
        Assert.Empty(h.Runs.Read());
    }

    // ---- the run record is finished exactly once

    [Theory, MemberData(nameof(Engines))]
    public async Task A_crash_of_the_executors_own_code_still_finishes_the_run_record(string engine)
    {
        using var h = new ExecutorHarness(engine, s =>
        {
            s.MaxScratchBytes = 1024;
            s.Logger = new ThrowingLogger();
        });
        var request = await h.SampleRequest("fills-and-exits");

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Executor.RunAsync(request));

        var run = Stored(h, request.RunId!.Value).Run;
        Assert.NotEqual(RunOutcome.Running, run.Outcome);
        Assert.DoesNotContain(RunOutcome.Running, h.Runs.Read().Select(r => r.Run.Outcome));
        Assert.Equal(RunOutcome.Failed, run.Outcome);
        Assert.Contains("The runner failed", run.Failure?.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(h.ScratchRoot));
    }

    [Fact]
    public async Task Runs_left_running_by_a_dead_runner_are_marked_interrupted_and_their_scratch_is_removed()
    {
        using var h = new ExecutorHarness(Engine.Sqlite);
        var dead = h.Runs.Start("sample/prod/ok", package: "sample", packageVersion: "0.9", channel: "prod", attempt: 1);
        var leftover = Path.Combine(h.ScratchRoot, "left-behind");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, "x.txt"), "x");
        await Task.Delay(50);

        var count = h.Executor.InterruptStaleRuns(DateTimeOffset.UtcNow);

        Assert.Equal(1, count);
        Assert.Equal(RunOutcome.Interrupted, Stored(h, dead).Run.Outcome);
        Assert.False(Directory.Exists(leftover));
    }

    // ---- in doubt

    [Theory, MemberData(nameof(Engines))]
    public async Task A_step_a_dead_attempt_left_started_is_put_in_doubt_before_the_retry_and_recovery_runs(string engine)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the test kills the process with SIGKILL");
        using var h = new ExecutorHarness(engine);
        var first = await h.SampleRequest("charge", s => s.Inputs = new Dictionary<string, string> { ["block"] = "true" });
        var firstRun = h.Executor.RunAsync(first);
        await h.WaitForLog(first.RunId!.Value, "in-step");
        var pid = await h.PidOf(first.RunId.Value);
        using (var process = Process.GetProcessById(pid)) process.Kill(entireProcessTree: true);
        var died = await firstRun;

        Assert.Equal(TaskFailureKind.ProcessExited, died.End.Failure!.Kind);
        Assert.Empty(died.InDoubtSteps); // the first attempt had nothing before it
        var steps = h.StepsOf("charge");
        var left = Assert.Single(steps.List(ExecutorHarness.Chain));
        Assert.Equal(StepState.Started, left.State);

        var retry = await h.SampleRequest("charge", s => s.Attempt = 2);
        var result = await h.Executor.RunAsync(retry);

        var doubt = Assert.Single(result.InDoubtSteps);
        Assert.Equal(("charge", "order-1", 1), (doubt.Name, doubt.Key, doubt.Attempt));
        Assert.Equal(StepState.InDoubt, doubt.State);
        Assert.True(result.End.State == TaskEndState.Succeeded, Why(h, retry, result));
        var log = h.Log(retry.RunId!.Value);
        var warning = Assert.Single(log, l => l.Level == "Warning" && l.Message.Contains("is in doubt", StringComparison.Ordinal));
        var check = Assert.Single(log, l => l.Message == "recovery-check-ran");
        Assert.True(warning.Id < check.Id, "the step was put in doubt before the job started");
        Assert.Contains(log, l => l.Message == "body-ran");
        var final = Assert.Single(steps.List(ExecutorHarness.Chain));
        Assert.Equal((StepState.Completed, 2), (final.State, final.Attempt));
    }

    // ---- helpers

    private static string Why(ExecutorHarness h, TaskRunRequest q, TaskRunResult r) => $"{r.End.State}: {r.End.Failure?.Kind} {r.End.Failure?.Message} (exit {r.ExitCode})" + "\n" + string.Join("\n", h.Log(r.RunId).Select(l => $"[{l.Level}] {l.Message}"));

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool Contains(byte[] haystack, string needle) => haystack.AsSpan().IndexOf(Encoding.UTF8.GetBytes(needle)) >= 0;

    private static bool ModeBitsDoNotBindThisUser()
    {
        if (OperatingSystem.IsWindows()) return false;
        var probe = Path.Combine(Path.GetTempPath(), "dbdatakit-probe-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(probe, "x");
        File.SetUnixFileMode(probe, UnixFileMode.UserRead);
        try
        {
            File.WriteAllText(probe, "y");
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            File.SetUnixFileMode(probe, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(probe);
        }
    }

    private static class Engine
    {
        public const string Sqlite = StateEngineIds.Sqlite;
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("the logger of the test refuses");
    }
}
