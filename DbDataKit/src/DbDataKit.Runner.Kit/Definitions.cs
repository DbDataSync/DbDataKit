using System.Reflection;

namespace DbDataKit.Runner.Kit;

/// <summary>Marks a job class as a task of its package; <see cref="Name"/> is the task's name in the manifest (plan 28b section 3, decision K8).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class RunnerTaskAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>A <see cref="Progress"/> member that is a position in a source: it has history and can be rewound (plan 29 section 4.3).</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class CheckpointAttribute : Attribute;

/// <summary>A <see cref="Progress"/> member that is state, not a position (a cursor token, a last-seen id): the same storage, without history and rewind.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class StateAttribute : Attribute;

/// <summary>No input: a job that takes none writes <c>RunnerJob&lt;NoInput, ...&gt;</c>.</summary>
public sealed record NoInput;

/// <summary>No progress: a job that keeps none writes <c>RunnerJob&lt;..., NoProgress, ...&gt;</c>.</summary>
public sealed class NoProgress : Progress;

/// <summary>No connections: a job that opens none writes <c>RunnerJob&lt;..., NoSlots&gt;</c>.</summary>
public sealed class NoSlots : Slots;

/// <summary>
/// A job's progress as a class (plan 28b section 5): public properties marked <see cref="CheckpointAttribute"/> or <see cref="StateAttribute"/>, and <see cref="Keyed{T}"/> for a set kept per key. The runner fills it
/// at start; setting a member marks it changed, and the changed values are saved with the run's outcome.
/// </summary>
public abstract class Progress;

/// <summary>
/// A set of progress objects, one per key (<c>Progress.Customer[c.Id].Modified</c>): the typed form of the declaration's <c>per: [customer]</c>. <typeparamref name="T"/> holds <see cref="CheckpointAttribute"/> and
/// <see cref="StateAttribute"/> members. The values of a key are read when it is first used, so a job with thousands of keys loads those it touches.
/// </summary>
public sealed class Keyed<T> : IKeyed where T : new()
{
    private readonly Dictionary<string, T> _items = new(StringComparer.Ordinal);
    private Func<string, object>? _loader;

    /// <summary>The progress of <paramref name="key"/>; a key never seen has the members' defaults.</summary>
    public T this[string key]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(key);
            lock (_items)
            {
                if (_items.TryGetValue(key, out var found)) return found;
                var item = _loader is null ? new T() : (T)_loader(key);
                _items[key] = item;
                return item;
            }
        }
    }

    Type IKeyed.ItemType => typeof(T);

    void IKeyed.Attach(Func<string, object> loader)
    {
        lock (_items)
        {
            _items.Clear();
            _loader = loader;
        }
    }
}

/// <summary>What the binder needs of a <see cref="Keyed{T}"/> without knowing T.</summary>
internal interface IKeyed
{
    Type ItemType { get; }

    void Attach(Func<string, object> loader);
}

/// <summary>The connections a job opens, as members of a class (<c>public Slot&lt;IWarehouse&gt; Warehouse { get; } = new("warehouse");</c>). The manifest's slots are generated from it.</summary>
public abstract class Slots
{
    internal SlotOpener? Opener { get; set; }
}

/// <summary>Opens the connection a slot is bound to: by slot name, whatever the channel binds it to. The job never sees a secret.</summary>
public delegate Task<object> SlotOpener(string slot, Type connectionType, CancellationToken ct);

/// <summary>What the manifest generator and the binder need of a <see cref="Slot{TConnection}"/> without knowing the connection type.</summary>
public abstract class SlotBase(string name, string kind, Type connectionType)
{
    public string Name { get; } = name;

    /// <summary>The kind of connection the slot takes, as the manifest writes it.</summary>
    public string Kind { get; } = kind;

    public Type ConnectionType { get; } = connectionType;

    internal Slots? Owner { get; set; }

    private protected async Task<object> OpenCoreAsync(CancellationToken ct)
    {
        var opener = Owner?.Opener ?? throw new InvalidOperationException($"The slot '{Name}' is not bound: a slot opens only inside a running job (JobRunner.RunAsync).");
        return await opener(Name, ConnectionType, ct).ConfigureAwait(false);
    }
}

/// <summary>A place a connection is plugged in: its name, and the type of handle the job gets (<typeparamref name="TConnection"/>).</summary>
public sealed class Slot<TConnection> : SlotBase
{
    /// <param name="name">The slot's name in the manifest.</param>
    /// <param name="kind">The connection kind the manifest declares; the type's name without a leading I, in lower case (<c>IWarehouse</c> is <c>warehouse</c>), when not given.</param>
    public Slot(string name, string? kind = null)
        : base(name, kind ?? DefaultKind(typeof(TConnection)), typeof(TConnection))
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
    }

    public async Task<TConnection> OpenAsync(CancellationToken ct) => (TConnection)await OpenCoreAsync(ct).ConfigureAwait(false);

    internal static string DefaultKind(Type type)
    {
        var name = type.Name;
        if (name.Length > 1 && name[0] == 'I' && char.IsUpper(name[1])) name = name[1..];
        return name.ToLowerInvariant();
    }
}

/// <summary>A counter a job declares once, as a static value; <see cref="Runner.Kit.RunnerJob{TInput,TProgress,TSlots}"/> counts against it.</summary>
public sealed record Counter(string Name);

/// <summary>What the kit does with a unit that throws.</summary>
public enum OnFailure
{
    /// <summary>Record it failed, go on with the others; the run ends <c>CompletedWithFailures</c> (the default).</summary>
    Continue,
    /// <summary>Stop at the first failure: the rest are recorded skipped and the run fails with that unit's error.</summary>
    Stop,
}

public sealed record UnitOptions(OnFailure OnFailure = OnFailure.Continue);

/// <summary>Declares steps: <c>Step.Value&lt;TIn, TOut&gt;(name, key)</c> and <c>Step.Stream&lt;TIn&gt;(name, key)</c>, as static values next to the job.</summary>
public static class Step
{
    /// <summary>A step whose result is a small typed value, kept as JSON. <typeparamref name="TOut"/> has to round-trip through System.Text.Json.</summary>
    public static ValueStep<TIn, TOut> Value<TIn, TOut>(string name, Func<TIn, string> key) => new(name, key);

    /// <summary>A step whose result is a payload written in chunks, never held in memory (a <see cref="PayloadRef"/>).</summary>
    public static StreamStep<TIn> Stream<TIn>(string name, Func<TIn, string> key, string? mediaType = null) => new(name, key, mediaType);
}

/// <summary>What the kit needs of a step definition without its types.</summary>
public abstract class StepDefinition
{
    private protected StepDefinition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }

    /// <summary>The input type, as the manifest lists it.</summary>
    public abstract Type InputType { get; }

    /// <summary>The result type: the value's type, or <see cref="PayloadRef"/> for a stream.</summary>
    public abstract Type ResultType { get; }

    public abstract bool IsStream { get; }
}

public sealed class ValueStep<TIn, TOut> : StepDefinition
{
    private readonly Func<TIn, string> _key;

    internal ValueStep(string name, Func<TIn, string> key)
        : base(name)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
    }

    public override Type InputType => typeof(TIn);

    public override Type ResultType => typeof(TOut);

    public override bool IsStream => false;

    public string KeyOf(TIn input) => _key(input) ?? throw new InvalidOperationException($"The key function of the step '{Name}' returned null.");
}

public sealed class StreamStep<TIn> : StepDefinition
{
    private readonly Func<TIn, string> _key;

    internal StreamStep(string name, Func<TIn, string> key, string? mediaType)
        : base(name)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
        MediaType = mediaType;
    }

    public override Type InputType => typeof(TIn);

    public override Type ResultType => typeof(PayloadRef);

    public override bool IsStream => true;

    public string? MediaType { get; }

    public string KeyOf(TIn input) => _key(input) ?? throw new InvalidOperationException($"The key function of the step '{Name}' returned null.");
}

/// <summary>Declares units: <c>Unit.Of&lt;Customer&gt;("customer", c =&gt; c.Id)</c>.</summary>
public static class Unit
{
    public static Unit<T> Of<T>(string name, Func<T, string> key) => new(name, key);
}

/// <summary>What the kit needs of a unit definition without its type.</summary>
public abstract class UnitDefinition
{
    private protected UnitDefinition(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }

    public abstract Type ItemType { get; }
}

public sealed class Unit<T> : UnitDefinition
{
    private readonly Func<T, string> _key;

    internal Unit(string name, Func<T, string> key)
        : base(name)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
    }

    public override Type ItemType => typeof(T);

    public string KeyOf(T item) => _key(item) ?? throw new InvalidOperationException($"The key function of the unit '{Name}' returned null.");
}

/// <summary>Finds the steps, units and counters a job class declares, so the console and the manifest can list them without running anything.</summary>
public static class JobDeclarations
{
    /// <summary>The steps, units and counters declared as static fields next to the job (see <see cref="Holders"/>).</summary>
    public static (IReadOnlyList<StepDefinition> Steps, IReadOnlyList<UnitDefinition> Units, IReadOnlyList<Counter> Counters) Find(Type job)
    {
        var steps = new List<StepDefinition>();
        var units = new List<UnitDefinition>();
        var counters = new List<Counter>();
        foreach (var holder in Holders(job))
            foreach (var field in holder.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var value = field.GetValue(null);
                switch (value)
                {
                    case StepDefinition step when steps.All(s => s.Name != step.Name): steps.Add(step); break;
                    case UnitDefinition unit when units.All(u => u.Name != unit.Name): units.Add(unit); break;
                    case Counter counter when counters.All(c => c.Name != counter.Name): counters.Add(counter); break;
                }
            }

        return (steps, units, counters);
    }

    /// <summary>
    /// The job class itself, its nested types, the classes its <see cref="RunnerDeclarationsAttribute"/> names, and static classes of its namespace named after it and ending in <c>Steps</c>, <c>Units</c> or
    /// <c>Counters</c> (<c>DailyDrop</c> with <c>DailyDropSteps</c>).
    /// </summary>
    private static IEnumerable<Type> Holders(Type job)
    {
        var seen = new HashSet<Type> { job };
        yield return job;
        foreach (var nested in job.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            if (seen.Add(nested)) yield return nested;
        foreach (var named in job.GetCustomAttributes<RunnerDeclarationsAttribute>(inherit: false).SelectMany(a => a.Holders))
            if (seen.Add(named)) yield return named;
        foreach (var type in job.Assembly.GetTypes())
            if (type.IsAbstract && type.IsSealed && type.DeclaringType is null && type.Namespace == job.Namespace && NamedAfter(type.Name, job.Name) && seen.Add(type))
                yield return type;
    }

    /// <summary><c>DailyDropSteps</c>, <c>InvoiceUnits</c> and <c>OrdersCounters</c> are named after the jobs <c>DailyDrop</c>, <c>Invoices</c> and <c>Orders</c>: the stem before the suffix is the job's name, give or take a plural s.</summary>
    private static bool NamedAfter(string holder, string job)
    {
        foreach (var suffix in new[] { "Steps", "Units", "Counters" })
            if (holder.EndsWith(suffix, StringComparison.Ordinal))
                return string.Equals(holder[..^suffix.Length].TrimEnd('s'), job.TrimEnd('s'), StringComparison.Ordinal);
        return false;
    }
}

/// <summary>Names the static classes that hold a job's steps, units and counters, when they are not named after the job (<c>DailyDropSteps</c> for <c>DailyDrop</c>).</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class RunnerDeclarationsAttribute(params Type[] holders) : Attribute
{
    public IReadOnlyList<Type> Holders { get; } = holders;
}
