namespace DbDataKit.Connections.Bindings;

/// <summary>How a value the console holds compares with the value another server holds for the same item (plan 27a IX25).</summary>
public enum CompareState
{
    /// <summary>Both hold the same value.</summary>
    Same,

    /// <summary>Both hold a value and the values differ.</summary>
    Differs,

    /// <summary>Only the console holds a value.</summary>
    OnlyHere,

    /// <summary>Only the other server holds a value.</summary>
    OnlyThere,
}

/// <summary>
/// A binding's choices as one short text, the form a group's binding row keeps them in (<c>bound_to</c> of <c>PUT /api/v1/bindings/{channel}/{kind}/{name}</c>, plan 30) and the form the compare screen shows (plan 27a IX25).
/// A choice is <c>connection</c> or, for a model slot, <c>connection/model</c> (a connection's name has no slash, so the first slash splits); several choices, the primary first, are separated by a comma and a space.
/// </summary>
public static class BindingValueText
{
    /// <summary>The text of the choices; empty for none.</summary>
    public static string Encode(IReadOnlyList<BindingEntry> entries) =>
        string.Join(", ", entries.Select(e => string.IsNullOrEmpty(e.Model) ? e.Connection : $"{e.Connection}/{e.Model}"));

    /// <summary>
    /// The choices a text says, or null with the reason. <paramref name="wantsModel"/> is true for a model slot (every choice names a model) and false for any other (none may); <paramref name="most"/> is how many choices the slot takes.
    /// </summary>
    public static IReadOnlyList<BindingEntry>? Decode(string? text, bool wantsModel, int most, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(text)) { problem = "There is no value to read."; return null; }
        var entries = new List<BindingEntry>();
        foreach (var part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var slash = part.IndexOf('/');
            var (connection, model) = slash < 0 ? (part, null) : (part[..slash], part[(slash + 1)..]);
            if (connection.Length == 0 || connection.Any(char.IsWhiteSpace)) { problem = $"'{part}' does not start with a connection's name."; return null; }
            if (wantsModel && string.IsNullOrEmpty(model)) { problem = $"'{part}' names no model: a model slot's choice is connection/model."; return null; }
            if (!wantsModel && model is not null) { problem = $"'{part}' names a model, and this slot takes a connection alone."; return null; }
            entries.Add(new BindingEntry(connection, model));
        }

        if (entries.Count == 0) { problem = "There is no value to read."; return null; }
        if (entries.Count > most) { problem = $"This slot takes {(most == 1 ? "one choice" : $"at most {most} choices")} and the value has {entries.Count}."; return null; }
        return entries;
    }
}

/// <summary>What the console compares with another server (plan 27a IX25, <c>docs/channel-bindings.md</c>).</summary>
public static class BindingCompare
{
    /// <summary>The state of one item from the two values (null is "holds none"). Values compare as written, so a caller normalises them first.</summary>
    public static CompareState Of(string? here, string? there) =>
        here is null && there is null ? CompareState.Same
        : there is null ? CompareState.OnlyHere
        : here is null ? CompareState.OnlyThere
        : string.Equals(here, there, StringComparison.Ordinal) ? CompareState.Same : CompareState.Differs;

    /// <summary>The prefix of a binding slot of another server that stands for one of the console's model slots: <c>model:default</c> is the console's model slot <c>default</c> (the name the registry gives an agent's model slot).</summary>
    public const string ModelPrefix = "model:";

    /// <summary>The prefix of a binding slot of another server that stands for one of the console's delivery channels: <c>delivery:alerts</c>.</summary>
    public const string DeliveryPrefix = "delivery:";

    /// <summary>
    /// The console slot a slot of another server's definition stands for, or null when it stands for none (a package's own connection slot is not a console slot). The section is <c>models</c> or <c>delivery</c> and the kind the one
    /// <see cref="BindingsLoader.ModelKindOf"/> gives a model slot of that name.
    /// </summary>
    public static (string Section, SlotKind Kind, string Name)? ConsoleSlotOf(string slot)
    {
        if (slot.StartsWith(ModelPrefix, StringComparison.Ordinal) && slot.Length > ModelPrefix.Length)
        {
            var name = slot[ModelPrefix.Length..];
            return ("models", BindingsLoader.ModelKindOf(name), name);
        }

        if (slot.StartsWith(DeliveryPrefix, StringComparison.Ordinal) && slot.Length > DeliveryPrefix.Length)
            return ("delivery", SlotKind.Delivery, slot[DeliveryPrefix.Length..]);
        return null;
    }

    /// <summary>The slot name another server uses for a console slot (the inverse of <see cref="ConsoleSlotOf"/>).</summary>
    public static string SlotNameOf(SlotKind kind, string name) => (kind == SlotKind.Delivery ? DeliveryPrefix : ModelPrefix) + name;
}
