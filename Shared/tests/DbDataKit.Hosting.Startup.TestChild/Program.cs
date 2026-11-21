using DbDataKit.Hosting.Startup;

// A server's start in a process of its own, for the tests that need the process's exit code or kill it in a step.
// Arguments: <logs folder> <data folder> ok | fail <step> | hang <step> <marker file>
// "fail" makes the step throw; "hang" writes the marker file once the step has begun, then waits until the process is killed.
var logs = args[0];
var data = args[1];
var mode = args[2];
var target = args.Length > 3 ? Enum.Parse<StartupStep>(args[3], ignoreCase: true) : (StartupStep?)null;
var marker = args.Length > 4 ? args[4] : null;

var sequence = new StartupSequence(new StartupOptions
{
    Service = SuiteServices.Runner,
    DataFolder = data,
    LogsRoot = logs,
    ExcludedPorts = () => [],
    Unreportable = text => Console.Error.WriteLine(text),
});
foreach (var step in StartupSteps.InOrder)
{
    sequence.Step(step, async (_, ct) =>
    {
        if (step != target) return;
        if (mode == "fail") throw new InvalidOperationException($"the {step.Name()} step of the test child fails");
        if (mode == "hang")
        {
            await File.WriteAllTextAsync(marker!, step.Name(), ct);
            await Task.Delay(Timeout.Infinite, ct);
        }
    });
}

var result = await sequence.RunAsync();
return result.ExitCode;
