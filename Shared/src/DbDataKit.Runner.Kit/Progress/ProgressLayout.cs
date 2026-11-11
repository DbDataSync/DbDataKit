using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DbDataKit.State;

namespace DbDataKit.Runner.Kit;

/// <summary>One member of a progress class that is kept: its property, its name in the declaration, its stored type, and whether it is a checkpoint (else state).</summary>
internal sealed record ProgressMember(PropertyInfo Property, string Name, ProgressType Type, bool IsCheckpoint);

/// <summary>A <see cref="Keyed{T}"/> member: kept per key; the item's checkpoint members are the parts of one checkpoint named after the member, its state members state values <c>member_state</c>.</summary>
internal sealed record KeyedMember(PropertyInfo Property, string Name, Type ItemType, IReadOnlyList<ProgressMember> Checkpoints, IReadOnlyList<ProgressMember> States)
{
    public string StateName(ProgressMember state) => Name + "_" + state.Name;
}

/// <summary>
/// What the kit reads from a <see cref="Progress"/> class by reflection: its kept members, and from them the declaration the manifest carries (plan 28b section 5). A flat <c>[Checkpoint]</c> member is a checkpoint
/// of its own with one part named <see cref="ValuePart"/>; a <c>[State]</c> member a state value; a <see cref="Keyed{T}"/> member a checkpoint per key.
/// </summary>
internal sealed class ProgressLayout
{
    /// <summary>The part a flat checkpoint member is stored as.</summary>
    public const string ValuePart = "value";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, ProgressLayout> Cache = new();

    private ProgressLayout(Type type, IReadOnlyList<ProgressMember> flat, IReadOnlyList<KeyedMember> keyed)
    {
        Type = type;
        Flat = flat;
        Keyed = keyed;
    }

    public Type Type { get; }

    public IReadOnlyList<ProgressMember> Flat { get; }

    public IReadOnlyList<KeyedMember> Keyed { get; }

    public static ProgressLayout For(Type progressType) => Cache.GetOrAdd(progressType, Build);

    private static ProgressLayout Build(Type type)
    {
        if (!typeof(Progress).IsAssignableFrom(type)) throw new InvalidOperationException($"{type.Name} is not a Progress class.");
        var flat = new List<ProgressMember>();
        var keyed = new List<KeyedMember>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (IsKeyed(property.PropertyType, out var item))
            {
                if (!property.CanRead) throw new InvalidOperationException($"{type.Name}.{property.Name} is a Keyed<{item.Name}> and has to be readable.");
                var members = Members(item, $"{type.Name}.{property.Name}");
                keyed.Add(new KeyedMember(property, Naming.Snake(property.Name), item, [.. members.Where(m => m.IsCheckpoint)], [.. members.Where(m => !m.IsCheckpoint)]));
                continue;
            }

            if (Member(property, type.Name) is { } member) flat.Add(member);
        }

        return new ProgressLayout(type, flat, keyed);
    }

    private static List<ProgressMember> Members(Type type, string owner)
    {
        var found = new List<ProgressMember>();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (IsKeyed(property.PropertyType, out _)) throw new InvalidOperationException($"{owner}: a Keyed<> inside a keyed item is not supported; one level of keys per member.");
            if (Member(property, owner) is { } member) found.Add(member);
        }

        return found;
    }

    private static ProgressMember? Member(PropertyInfo property, string owner)
    {
        var checkpoint = property.GetCustomAttribute<CheckpointAttribute>() is not null;
        var state = property.GetCustomAttribute<StateAttribute>() is not null;
        if (checkpoint && state) throw new InvalidOperationException($"{owner}.{property.Name} is marked both [Checkpoint] and [State]; choose one.");
        if (!checkpoint && !state) return null;
        if (!property.CanRead || !property.CanWrite) throw new InvalidOperationException($"{owner}.{property.Name} is marked [{(checkpoint ? "Checkpoint" : "State")}] and needs a public getter and setter.");
        return new ProgressMember(property, Naming.Snake(property.Name), TypeMap.Of(property.PropertyType), checkpoint);
    }

    private static bool IsKeyed(Type type, out Type item)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Keyed<>))
        {
            item = type.GetGenericArguments()[0];
            return true;
        }

        item = typeof(void);
        return false;
    }

    /// <summary>The declaration of the manifest (plan 29 section 4.3) that this class stands for.</summary>
    public ProgressDeclaration ToDeclaration()
    {
        var checkpoints = new List<DeclaredCheckpoint>();
        var states = new List<DeclaredState>();
        foreach (var member in Flat)
        {
            if (member.IsCheckpoint) checkpoints.Add(new DeclaredCheckpoint(member.Name, new Dictionary<string, DeclaredPart> { [ValuePart] = new(member.Type) }));
            else states.Add(new DeclaredState(member.Name, member.Type));
        }

        foreach (var keyed in Keyed)
        {
            if (keyed.Checkpoints.Count > 0)
                checkpoints.Add(new DeclaredCheckpoint(keyed.Name, keyed.Checkpoints.ToDictionary(m => m.Name, m => new DeclaredPart(m.Type)), [keyed.Name]));
            foreach (var state in keyed.States) states.Add(new DeclaredState(keyed.StateName(state), state.Type, [keyed.Name]));
        }

        return new ProgressDeclaration(checkpoints, states);
    }
}

internal static class Naming
{
    /// <summary><c>OrdersModified</c> becomes <c>orders_modified</c>.</summary>
    public static string Snake(string name)
    {
        var text = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                var startsWord = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1])));
                if (startsWord) text.Append('_');
                text.Append(char.ToLowerInvariant(c));
            }
            else text.Append(c);
        }

        return text.ToString();
    }
}

/// <summary>The mapping between a CLR type and a stored <see cref="ProgressType"/>, and the conversion of the values.</summary>
internal static class TypeMap
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ProgressType Of(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type.IsEnum || type == typeof(Guid)) return ProgressType.Text;
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(uint)) return ProgressType.Int;
        if (type == typeof(decimal) || type == typeof(double) || type == typeof(float)) return ProgressType.Decimal;
        if (type == typeof(DateTimeOffset) || type == typeof(DateTime)) return ProgressType.Time;
        if (type == typeof(DateOnly)) return ProgressType.Date;
        if (type == typeof(bool)) return ProgressType.Bool;
        if (type == typeof(PayloadRef)) return ProgressType.Bytes;
        return ProgressType.Json;
    }

    /// <summary>The stored value of a member's current value; null for no value (a null reference or nullable).</summary>
    public static ProgressValue? ToValue(object? value, Type declared, ProgressType type)
    {
        if (value is null) return null;
        return type switch
        {
            ProgressType.Text => ProgressValue.OfText(value is Enum or Guid ? Convert.ToString(value, CultureInfo.InvariantCulture)! : (string)value),
            ProgressType.Int => ProgressValue.OfInt(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            ProgressType.Decimal => ProgressValue.OfDecimal(Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
            ProgressType.Time => ProgressValue.OfTime(value is DateTime time ? new DateTimeOffset(time.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : time.ToUniversalTime()) : (DateTimeOffset)value),
            ProgressType.Date => ProgressValue.OfDate((DateOnly)value),
            ProgressType.Bool => ProgressValue.OfBool((bool)value),
            ProgressType.Bytes => ProgressValue.OfBytes(((PayloadRef)value).Id),
            _ => ProgressValue.OfJson(JsonSerializer.Serialize(value, declared, Json)),
        };
    }

    /// <summary>Whether the member still holds what a new object holds, so "never set" can be told from "set": only a value type has a default that is also a value.</summary>
    public static bool IsDefault(object? value, Type declared) => value is null || (declared.IsValueType && Nullable.GetUnderlyingType(declared) is null && Equals(value, Activator.CreateInstance(declared)));

    public static object? FromValue(ProgressValue value, Type declared, Func<Guid, PayloadRef> payloads)
    {
        var target = Nullable.GetUnderlyingType(declared) ?? declared;
        switch (value.Type)
        {
            case ProgressType.Text:
                return target == typeof(string) ? value.AsText : target == typeof(Guid) ? Guid.Parse(value.AsText, CultureInfo.InvariantCulture) : Enum.Parse(target, value.AsText);
            case ProgressType.Int: return Convert.ChangeType(value.AsInt, target, CultureInfo.InvariantCulture);
            case ProgressType.Decimal: return Convert.ChangeType(value.AsDecimal, target, CultureInfo.InvariantCulture);
            case ProgressType.Time: return target == typeof(DateTime) ? value.AsTime.UtcDateTime : value.AsTime;
            case ProgressType.Date: return value.AsDate;
            case ProgressType.Bool: return value.AsBool;
            case ProgressType.Bytes: return payloads(value.AsPayload);
            default: return JsonSerializer.Deserialize(value.AsText, target, Json);
        }
    }

    public static bool Same(ProgressValue? a, ProgressValue? b) =>
        a is null ? b is null : b is not null && a.Type == b.Type && a.Canonical() == b.Canonical();
}
