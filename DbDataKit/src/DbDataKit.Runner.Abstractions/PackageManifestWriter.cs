using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DbDataKit.Monitoring;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.Runner;

/// <summary>Writes a <see cref="PackageManifest"/> as <c>package/1</c> text that <see cref="PackageManifestReader"/> reads back to an equal manifest. Defaults are left out; key order is fixed, so the same manifest always gives the same text.</summary>
public static partial class PackageManifestWriter
{
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_./@-]*$")]
    private static partial Regex Plain();

    public static string Write(PackageManifest manifest)
    {
        var w = new StringBuilder();
        w.Append("schema: ").Append(PackageManifest.FormatId).Append('\n');
        w.Append("name: ").Append(Q(manifest.Name)).Append('\n');
        if (manifest.Description is not null) w.Append("description: ").Append(Q(manifest.Description)).Append('\n');

        w.Append("launch: ").Append(Flow(
            ("kind", manifest.Launch.Kind == LaunchKind.Assembly ? "assembly" : "executable"),
            ("assembly", S(manifest.Launch.Assembly)),
            ("command", S(manifest.Launch.Command)),
            ("args", List(manifest.Launch.ArgList)))).Append('\n');

        var built = manifest.BuiltAgainst;
        if (built.Dotnet is not null || built.Kit is not null || built.Protocol is not null)
            w.Append("built_against: ").Append(Flow(("dotnet", D(built.Dotnet)), ("kit", D(built.Kit)), ("protocol", S(built.Protocol)))).Append('\n');

        w.Append("tasks:\n");
        foreach (var task in manifest.Tasks) Task(w, task);
        return w.ToString();
    }

    private static void Task(StringBuilder w, PackageTask task)
    {
        w.Append("  - name: ").Append(Q(task.Name)).Append('\n');
        if (task.Entry.Type is not null || task.Entry.Method != TaskEntry.DefaultMethod || task.Entry.ArgList.Count > 0)
            w.Append("    entry: ").Append(Flow(("type", S(task.Entry.Type)), ("method", task.Entry.Method == TaskEntry.DefaultMethod ? null : S(task.Entry.Method)), ("args", List(task.Entry.ArgList)))).Append('\n');

        var defaults = new Schedule();
        var s = task.Schedule;
        if (s != defaults)
            w.Append("    schedule: ").Append(Flow(
                ("cron", S(s.Cron)),
                ("every", s.Every is { } every ? Duration(every) : null),
                ("time_zone", s.TimeZoneId == defaults.TimeZoneId ? null : S(s.TimeZoneId)),
                ("misfire", s.Misfire == defaults.Misfire ? null : s.Misfire == MisfirePolicy.Skip ? "skip" : "run_once"),
                ("misfire_grace", s.MisfireGrace == defaults.MisfireGrace ? null : Duration(s.MisfireGrace)))).Append('\n');

        if (task.AllowOverlap) w.Append("    allow_overlap: true\n");

        if (task.Triggers.Count > 0)
        {
            w.Append("    triggers:\n");
            foreach (var t in task.Triggers) w.Append("      - ").Append(Trigger(t)).Append('\n');
        }

        if (task.Inputs.Count > 0)
        {
            w.Append("    inputs:\n");
            foreach (var i in task.Inputs)
                w.Append("      - ").Append(Flow(("name", S(i.Name)), ("type", i.Type == ProgressType.Text ? null : PackageManifestValidator.TypeWord(i.Type)), ("required", i.Required ? "true" : null), ("default", D(i.Default)), ("description", S(i.Description)))).Append('\n');
        }

        if (task.Connections.Count > 0)
        {
            w.Append("    connections:\n");
            foreach (var c in task.Connections) w.Append("      - ").Append(Flow(("name", S(c.Name)), ("kind", S(c.Kind)))).Append('\n');
        }

        Progress(w, task.Progress);

        var l = task.Limits;
        if (l != TaskLimits.None)
            w.Append("    limits: ").Append(Flow(
                ("timeout", l.Timeout is { } timeout ? Duration(timeout) : null),
                ("memory_mb", l.MemoryMb?.ToString(CultureInfo.InvariantCulture)),
                ("cpu", l.Cpu?.ToString(CultureInfo.InvariantCulture)),
                ("concurrency", l.Concurrency?.ToString(CultureInfo.InvariantCulture)),
                ("attempts", l.Attempts?.ToString(CultureInfo.InvariantCulture)),
                ("backoff", l.Backoff is { } backoff ? Duration(backoff) : null),
                ("max_backoff", l.MaxBackoff is { } max ? Duration(max) : null))).Append('\n');

        if (task.StepList.Count > 0)
        {
            w.Append("    steps:\n");
            foreach (var step in task.StepList)
                w.Append("      - ").Append(Flow(("name", S(step.Name)), ("kind", step.Kind == StepKind.Stream ? "stream" : null), ("input", S(step.Input)), ("result", S(step.Result)))).Append('\n');
        }

        if (task.UnitList.Count > 0)
        {
            w.Append("    units:\n");
            foreach (var unit in task.UnitList) w.Append("      - ").Append(Flow(("name", S(unit.Name)), ("item", S(unit.Item)))).Append('\n');
        }

        if (task.CounterList.Count > 0) w.Append("    counters: ").Append(List(task.CounterList)).Append('\n');

        if (task.Requires.Count > 0) w.Append("    requires: ").Append(List(task.Requires)).Append('\n');
        if (task.Capabilities.Count > 0) w.Append("    capabilities: ").Append(List(task.Capabilities)).Append('\n');
    }

    private static string Trigger(TaskTrigger t) => t.Kind switch
    {
        TriggerKind.AfterRun => "after: " + Flow(("task", S(t.Task)), ("package", S(t.Package)), ("on", t.On is { Count: > 0 } on ? ListOf(on.Select(o => PackageManifestReader.OutcomeWords.First(w => w.Outcome == o).Word)) : null)),
        TriggerKind.Webhook => "webhook: " + Flow(("source", S(t.Source))),
        TriggerKind.File => "file: " + Flow(("connection", S(t.Connection)), ("path", S(t.Path)), ("poll", t.Poll is { } poll ? Duration(poll) : null)),
        TriggerKind.StoreChange => "store_change: " + Flow(("model", S(t.Model)), ("entity", S(t.Entity))),
        _ => "manual: true",
    };

    private static void Progress(StringBuilder w, ProgressDeclaration progress)
    {
        if (progress.Checkpoints.Count == 0 && progress.States.Count == 0) return;
        w.Append("    progress:\n");
        if (progress.Checkpoints.Count > 0)
        {
            w.Append("      checkpoints:\n");
            foreach (var c in progress.Checkpoints)
            {
                w.Append("        - name: ").Append(Q(c.Name)).Append('\n');
                if (c.Per is { Count: > 0 }) w.Append("          per: ").Append(ListOf(c.Per)).Append('\n');
                if (c.RenamedFrom is not null) w.Append("          renamed_from: ").Append(Q(c.RenamedFrom)).Append('\n');
                if (c.Reset) w.Append("          reset: true\n");
                if (c.KeyFill is not null) w.Append("          fill: ").Append(Q(c.KeyFill)).Append('\n');
                w.Append("          parts:\n");
                foreach (var (name, part) in c.Parts)
                    w.Append("            - ").Append(Flow(("name", S(name)), ("type", PackageManifestValidator.TypeWord(part.Type)), ("default", D(part.Default)), ("renamed_from", S(part.RenamedFrom)), ("format", D(part.Format)), ("reset", part.Reset ? "true" : null))).Append('\n');
            }
        }

        if (progress.States.Count > 0)
        {
            w.Append("      state:\n");
            foreach (var s in progress.States)
                w.Append("        - ").Append(Flow(("name", S(s.Name)), ("type", PackageManifestValidator.TypeWord(s.Type)), ("per", s.Per is { Count: > 0 } ? ListOf(s.Per) : null), ("default", D(s.Default)), ("renamed_from", S(s.RenamedFrom)), ("format", D(s.Format)), ("reset", s.Reset ? "true" : null), ("fill", S(s.KeyFill)))).Append('\n');
        }
    }

    /// <summary>A length of time in the largest unit that holds it exactly.</summary>
    private static string Duration(TimeSpan span)
    {
        var ms = span.TotalMilliseconds;
        string Show(double value, string unit) => value.ToString("0.###############", CultureInfo.InvariantCulture) + unit;
        if (ms != 0 && ms % 86_400_000 == 0) return Show(ms / 86_400_000, "d");
        if (ms != 0 && ms % 3_600_000 == 0) return Show(ms / 3_600_000, "h");
        if (ms != 0 && ms % 60_000 == 0) return Show(ms / 60_000, "m");
        if (ms % 1000 == 0) return Show(ms / 1000, "s");
        return Show(ms, "ms");
    }

    /// <summary>A flow list <c>[a, b]</c> of text, or null when it is empty.</summary>
    private static string? List(IEnumerable<string> items) => ListOf(items);

    private static string? ListOf(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? null : "[" + string.Join(", ", list.Select(Q)) + "]";
    }

    /// <summary>A flow mapping of the entries that have a value; each value is already as it is written (see <see cref="S"/>, <see cref="D"/>).</summary>
    private static string Flow(params (string Key, string? Value)[] entries) =>
        "{ " + string.Join(", ", entries.Where(e => e.Value is not null).Select(e => $"{e.Key}: {e.Value}")) + " }";

    /// <summary>Text, quoted when it has to be.</summary>
    private static string? S(string? value) => value is null ? null : Q(value);

    /// <summary>Text, always quoted: a default or a version, which another YAML reader would take for a number or a flag.</summary>
    private static string? D(string? value) => value is null ? null : Quoted(value);

    private static string Q(string text) => Plain().IsMatch(text) && text is not ("true" or "false" or "null" or "~") ? text : Quoted(text);

    private static string Quoted(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";
}
