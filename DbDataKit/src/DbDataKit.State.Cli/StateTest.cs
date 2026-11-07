using DbDataKit.Cli;

namespace DbDataKit.State.Cli;

/// <summary>The <c>state test</c> verb over <see cref="StateChecks"/>: prints each step, and on a failure the one sentence that names the fix.</summary>
internal static class StateTest
{
    public static async Task<int> RunAsync(StateVerbContext context, string[] args, CliContext output)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string>(context.Options));
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await output.Error.WriteLineAsync($"Usage: state test {context.OptionsUsage}".TrimEnd());
            return CliExit.Usage;
        }

        StateSession session;
        try { session = StateSession.Create(context, parsed); }
        catch (StateSettingsProblem ex)
        {
            await output.Error.WriteLineAsync(ex.Message);
            output.Data["ok"] = false;
            output.Data["steps"] = new List<object?> { new Dictionary<string, object?> { ["name"] = "settings", ["status"] = "failed", ["message"] = ex.Message } };
            output.Data["failed"] = "settings";
            return StateExit.Settings;
        }

        var report = StateChecks.Run(context, session);
        foreach (var step in report.Steps)
            await output.Out.WriteLineAsync($"{step.Status.ToUpperInvariant(),-7} {step.Name,-11} {session.Scrub(step.Message)}");
        if (report.Failed is { } failed)
            await output.Error.WriteLineAsync(session.Scrub(failed.Message));

        output.Data["engine"] = report.Engine;
        output.Data["ok"] = report.Ok;
        output.Data["steps"] = report.Steps.Select(s => (object?)new Dictionary<string, object?> { ["name"] = s.Name, ["status"] = s.Status, ["message"] = session.Scrub(s.Message) }).ToList();
        if (report.Failed is { } f) output.Data["failed"] = f.Name;
        return report.Exit;
    }
}
