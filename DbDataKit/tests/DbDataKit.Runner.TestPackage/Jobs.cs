using System.Diagnostics;
using DbDataKit.Runner.Kit;

namespace DbDataKit.Runner.TestPackage;

// Jobs whose behaviour the executor has to get right. Each writes the lines the tests wait for to standard output.

/// <summary>Succeeds, with a count.</summary>
[RunnerTask("ok")]
public sealed class Ok : RunnerJob<NoInput, NoProgress, NoSlots>
{
    private static readonly Counter Done = new("done");

    protected override Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine("hello from ok");
        Console.Error.WriteLine("a warning on standard error");
        Count(Done, 5);
        return Task.CompletedTask;
    }
}

public sealed class TallyProgress : Progress
{
    [Checkpoint]
    public long Seen { get; set; }
}

/// <summary>Counts its own runs in a checkpoint: prints what it saw, then moves it by one. Staging tests read the checkpoint to see what a migration did to it.</summary>
[RunnerTask("tally")]
public sealed class Tally : RunnerJob<NoInput, TallyProgress, NoSlots>
{
    protected override Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine($"tally saw {Progress.Seen}");
        Progress.Seen += 1;
        return Task.CompletedTask;
    }
}

/// <summary>Runs for a few seconds and ends by itself: a run in flight while a version is activated.</summary>
[RunnerTask("lingers")]
public sealed class Lingers : RunnerJob<NoInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine("lingering");
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
    }
}

/// <summary>Throws: the kit classifies it as a permanent failure and sends the end message.</summary>
[RunnerTask("fails")]
public sealed class Fails : RunnerJob<NoInput, NoProgress, NoSlots>
{
    protected override Task RunAsync(CancellationToken ct) => throw new InvalidOperationException("the job broke");
}

/// <summary>Prints that it is ready and waits for the stop request; stops promptly when it comes.</summary>
[RunnerTask("waits")]
public sealed class Waits : RunnerJob<NoInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine("ready");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        finally
        {
            Console.WriteLine("stopping on request");
        }
    }
}

/// <summary>Ignores the stop request and the time: blocks a thread.</summary>
[RunnerTask("stubborn")]
public sealed class Stubborn : RunnerJob<NoInput, NoProgress, NoSlots>
{
    protected override Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine("ready");
        // a foreground thread: the process does not exit until it is killed, even after the task host sent the end message
        var done = new TaskCompletionSource();
        new Thread(() =>
        {
            Thread.Sleep(TimeSpan.FromMinutes(2));
            done.SetResult();
        })
        { IsBackground = false }.Start();
        return done.Task;
    }
}

/// <summary>Exits the process with code 0 in the middle of the job: no end message.</summary>
[RunnerTask("exits-zero")]
public sealed class ExitsZero : RunnerJob<NoInput, NoProgress, NoSlots>
{
    protected override Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine("about to exit silently");
        Environment.Exit(0);
        return Task.CompletedTask;
    }
}

/// <summary>Exits the process with code 3 in the middle of the job: no end message.</summary>
[RunnerTask("exits-three")]
public sealed class ExitsThree : RunnerJob<NoInput, NoProgress, NoSlots>
{
    protected override Task RunAsync(CancellationToken ct)
    {
        Environment.Exit(3);
        return Task.CompletedTask;
    }
}

/// <summary>Succeeds, and then kills its own process (SIGKILL) a moment later, after the end message went out.</summary>
[RunnerTask("crashes-after-end")]
public sealed class CrashesAfterEnd : RunnerJob<NoInput, NoProgress, NoSlots>
{
    protected override Task RunAsync(CancellationToken ct)
    {
        // a foreground thread keeps the process alive until it has killed it, whatever the task host does when the run ends
        new Thread(() =>
        {
            Thread.Sleep(1500);
            Process.GetCurrentProcess().Kill();
        })
        { IsBackground = false }.Start();
        Console.WriteLine("done; crashing soon");
        return Task.CompletedTask;
    }
}

public sealed record AllocationInput(int Megabytes = 1500);

/// <summary>Allocates and touches memory in steps until it is killed.</summary>
[RunnerTask("allocates")]
public sealed class Allocates : RunnerJob<AllocationInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var held = new List<byte[]>();
        for (var mb = 0; mb < Input.Megabytes; mb += 25)
        {
            var block = new byte[25 * 1024 * 1024];
            Array.Fill(block, (byte)1);
            held.Add(block);
            await Task.Delay(30, CancellationToken.None);
        }

        Console.WriteLine($"allocated {held.Count * 25} MB without being stopped");
    }
}

public sealed record SecretInput(string Secret);

/// <summary>Prints the secret it was given (as itself, inside JSON, and as part of a connection string) on both output streams.</summary>
[RunnerTask("prints-secret")]
public sealed class PrintsSecret : RunnerJob<SecretInput, NoProgress, NoSlots>
{
    protected override Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine($"the password is {Input.Secret}");
        Console.Error.WriteLine($"{{\"token\":\"{Input.Secret}\"}}");
        Console.WriteLine($"Server=db;User Id=sa;Password={Input.Secret};");
        Console.WriteLine("and a harmless line");
        return Task.CompletedTask;
    }
}

/// <summary>Prints what the process can see of its surroundings, and writes a file to its scratch directory.</summary>
[RunnerTask("probe")]
public sealed class Probe : RunnerJob<NoInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var scratch = Environment.GetEnvironmentVariable("DBDATAKIT_SCRATCH_DIR");
        Console.WriteLine($"CWD={Directory.GetCurrentDirectory()}");
        Console.WriteLine($"SCRATCH={scratch}");
        Console.WriteLine($"TMP={Path.GetTempPath()}");
        Console.WriteLine($"PACKAGE={Environment.GetEnvironmentVariable("DBDATAKIT_PACKAGE_DIR")}");
        Console.WriteLine($"STATE_VAR={Environment.GetEnvironmentVariable("RUNNER_STATE_PASSWORD") ?? "unset"}");
        await File.WriteAllTextAsync(Path.Combine(scratch!, "work.txt"), "scratch content", ct);
        await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "temp.txt"), "temp content", ct);
        Console.WriteLine("ready");
        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>Writes a lot into its scratch directory.</summary>
[RunnerTask("fills-scratch")]
public sealed class FillsScratch : RunnerJob<NoInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var scratch = Environment.GetEnvironmentVariable("DBDATAKIT_SCRATCH_DIR")!;
        await File.WriteAllBytesAsync(Path.Combine(scratch, "big.bin"), new byte[3 * 1024 * 1024], ct);
        var locked = Path.Combine(scratch, "locked");
        Directory.CreateDirectory(locked);
        await File.WriteAllTextAsync(Path.Combine(locked, "readonly.txt"), "x", ct);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(locked, "readonly.txt"), UnixFileMode.UserRead);
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
    }
}

/// <summary>Writes into its scratch directory and then exits without an end message.</summary>
[RunnerTask("fills-and-exits")]
public sealed class FillsAndExits : RunnerJob<NoInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var scratch = Environment.GetEnvironmentVariable("DBDATAKIT_SCRATCH_DIR")!;
        await File.WriteAllBytesAsync(Path.Combine(scratch, "big.bin"), new byte[3 * 1024 * 1024], ct);
        Environment.Exit(5);
    }
}

/// <summary>Tries to write into the package's directory, as a job that misbehaves would; succeeds only when every attempt was refused.</summary>
[RunnerTask("writes-package")]
public sealed class WritesPackage : RunnerJob<NoInput, NoProgress, NoSlots>
{
    protected override Task RunAsync(CancellationToken ct)
    {
        var package = Environment.GetEnvironmentVariable("DBDATAKIT_PACKAGE_DIR")!;
        var existing = Directory.EnumerateFiles(package, "*.dll").First();
        var attempts = new (string What, Action Try)[]
        {
            ("create a file", () => File.WriteAllText(Path.Combine(package, "intruder.txt"), "x")),
            ("overwrite a file", () => File.WriteAllText(existing, "x")),
            ("delete a file", () => File.Delete(existing)),
        };
        var allowed = new List<string>();
        foreach (var (what, attempt) in attempts)
        {
            try
            {
                attempt();
                allowed.Add(what);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Console.WriteLine($"refused: {what} ({ex.GetType().Name})");
            }
        }

        if (allowed.Count > 0) throw new InvalidOperationException("the package directory allowed: " + string.Join(", ", allowed));
        return Task.CompletedTask;
    }
}

public sealed record ChargeInput(bool Block = false);

public static class ChargeSteps
{
    public static readonly ValueStep<string, string> Charge = Step.Value<string, string>("charge", id => id);
}

/// <summary>One step with a check: attempt 1 blocks inside the step (to be killed), attempt 2 recovers.</summary>
[RunnerTask("charge")]
public sealed class Charge : RunnerJob<ChargeInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var receipt = await Step(ChargeSteps.Charge, "order-1")
            .Check(ct =>
            {
                Console.WriteLine("recovery-check-ran");
                return Task.FromResult<string?>(null);
            })
            .RunAsync(async ct =>
            {
                Console.WriteLine("in-step");
                if (Input.Block) await Task.Delay(Timeout.Infinite, CancellationToken.None);
                Console.WriteLine("body-ran");
                return "charged";
            }, ct);
        Console.WriteLine($"receipt {receipt}");
    }
}
