using System.Reflection;
using DbDataKit.Scheduling;
using DbDataKit.State;

namespace DbDataKit.Runner.Kit;

/// <summary>
/// Generates a package manifest from the job classes of an assembly (plan 28b decision K8): for a .NET job the C# types are the source of truth, and the manifest's task entry is made from them at publish. The
/// schedule, triggers, limits, labels and capabilities are not in the types: they are the publisher's, and a manifest this makes has none (<see cref="PackageTask"/> defaults), to be filled in by whoever
/// publishes. The staging check compares the manifest with what the assembly declares either way.
/// </summary>
public static class ManifestGenerator
{
    /// <summary>The manifest for every <c>[RunnerTask]</c> job class of <paramref name="assembly"/>, in the order of their task names.</summary>
    /// <param name="launch">How the package starts (normally <c>new PackageLaunch(LaunchKind.Assembly, "bin/Orders.dll")</c>).</param>
    /// <param name="builtAgainst">What the package is built against; the running .NET version, kit 1 and protocol v1 when not given.</param>
    public static PackageManifest ForAssembly(Assembly assembly, string packageName, PackageLaunch launch, string? description = null, BuiltAgainst? builtAgainst = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        ArgumentNullException.ThrowIfNull(launch);
        var jobs = assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(RunnerJobBase).IsAssignableFrom(t) && t.GetCustomAttribute<RunnerTaskAttribute>(inherit: false) is not null)
            .OrderBy(t => JobRunner.TaskNameOf(t), StringComparer.Ordinal)
            .ToList();
        var built = builtAgainst ?? new BuiltAgainst($"{Environment.Version.Major}.{Environment.Version.Minor}", "1", RunnerProtocol.V1);
        return new PackageManifest(packageName, description, launch, built, [.. jobs.Select(TaskOf)]);
    }

    /// <summary>The task entry of one job class.</summary>
    public static PackageTask TaskOf(Type job)
    {
        if (!typeof(RunnerJobBase).IsAssignableFrom(job) || job.IsAbstract) throw new ArgumentException($"{job.Name} is not a job class.", nameof(job));
        var instance = (RunnerJobBase)(Activator.CreateInstance(job, nonPublic: true) ?? throw new InvalidOperationException($"{job.Name} cannot be created."));
        var (steps, units, counters) = JobDeclarations.Find(job);
        var slots = (Slots)Activator.CreateInstance(instance.SlotsType)!;
        return new PackageTask(
            JobRunner.TaskNameOf(job),
            new TaskEntry((job.FullName ?? job.Name).Replace('+', '.')),
            new Schedule(),
            [],
            Inputs(instance.InputType),
            [.. SlotsOf(slots).Select(s => new ConnectionSlot(s.Name, s.Kind))],
            ProgressLayout.For(instance.ProgressType).ToDeclaration(),
            TaskLimits.None,
            [],
            [],
            false,
            steps.Count == 0 ? null : [.. steps.Select(s => new TaskStep(s.Name, s.IsStream ? StepKind.Stream : StepKind.Value, Friendly(s.InputType), s.IsStream ? null : Friendly(s.ResultType)))],
            units.Count == 0 ? null : [.. units.Select(u => new TaskUnit(u.Name, Friendly(u.ItemType)))],
            counters.Count == 0 ? null : [.. counters.Select(c => c.Name)]);
    }

    private static IEnumerable<SlotBase> SlotsOf(Slots slots)
    {
        foreach (var property in slots.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (typeof(SlotBase).IsAssignableFrom(property.PropertyType) && property.GetValue(slots) is SlotBase slot) yield return slot;
        foreach (var field in slots.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (typeof(SlotBase).IsAssignableFrom(field.FieldType) && field.GetValue(slots) is SlotBase slot) yield return slot;
    }

    /// <summary>The input record's properties are the manifest's <c>inputs</c>: a nullable property, or one with a default in the record's constructor, is optional.</summary>
    private static List<TaskInput> Inputs(Type input)
    {
        var inputs = new List<TaskInput>();
        if (input == typeof(NoInput)) return inputs;
        var constructor = input.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        var nullability = new NullabilityInfoContext();
        foreach (var property in input.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0 && p.DeclaringType == input))
        {
            if (property.Name == "EqualityContract") continue;
            var parameter = constructor?.GetParameters().FirstOrDefault(p => string.Equals(p.Name, property.Name, StringComparison.Ordinal));
            var optionalType = Nullable.GetUnderlyingType(property.PropertyType) is not null || nullability.Create(property).ReadState == NullabilityState.Nullable;
            string? @default = null;
            if (parameter is { HasDefaultValue: true, DefaultValue: { } value }) @default = TypeMap.ToValue(value, property.PropertyType, TypeMap.Of(property.PropertyType))?.Canonical();
            inputs.Add(new TaskInput(Naming.Snake(property.Name), TypeMap.Of(property.PropertyType), Required: !optionalType && !(parameter?.HasDefaultValue ?? false), Default: @default));
        }

        return inputs;
    }

    /// <summary>A type name for people: <c>List&lt;Row&gt;</c>, not <c>List`1</c>.</summary>
    private static string Friendly(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } inner) return Friendly(inner) + "?";
        if (!type.IsGenericType) return type.Name;
        var name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return name + "<" + string.Join(", ", type.GetGenericArguments().Select(Friendly)) + ">";
    }
}
