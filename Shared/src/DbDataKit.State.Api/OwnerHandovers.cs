using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;

namespace DbDataKit.State.Api;

/// <summary>One hand-over: what the client <see cref="From"/> owns belongs to <see cref="To"/> from <see cref="At"/>, as <see cref="By"/> asked.</summary>
public sealed record OwnerHandover(string From, string To, string By, DateTimeOffset At);

/// <summary>
/// The hand-overs of a group (Design: docs/publishing.md#taking-over-what-a-workspace-key-published): an administrator says that what one paired client owns now belongs to another, and every owner check reads the owner through
/// <see cref="Resolve(string?)"/>. Nothing a client published is rewritten: a signed version keeps the <c>managedBy</c> it was signed with, and the group's settings say who owns it now. A client handed over owns nothing afterwards; what it
/// publishes later belongs to the client it was handed to as well. The hand-overs are one setting of the group (<c>owner-handovers</c>, a JSON object by client id), so every instance reads the same ones.
/// </summary>
public sealed class OwnerHandovers(IGroupSettings settings)
{
    public const string Setting = "owner-handovers";

    /// <summary>Every hand-over, by the client handed over.</summary>
    public IReadOnlyDictionary<string, OwnerHandover> All() => Parse(settings.Get(Setting));

    /// <summary>Who owns what <paramref name="owner"/> names now: the client it was handed to (followed through a later hand-over of that client), or <paramref name="owner"/> itself. Null and empty stay as they are (made by hand).</summary>
    public string? Resolve(string? owner) => Resolve(owner, All());

    /// <summary>The same as <see cref="Resolve(string?)"/> over hand-overs already read, for a caller that resolves many owners at once.</summary>
    public static string? Resolve(string? owner, IReadOnlyDictionary<string, OwnerHandover> handovers)
    {
        if (string.IsNullOrEmpty(owner)) return owner;
        var seen = new HashSet<string>(StringComparer.Ordinal) { owner };
        while (handovers.TryGetValue(owner, out var next) && seen.Add(next.To)) owner = next.To;
        return owner;
    }

    /// <summary>Records that what <paramref name="from"/> owns belongs to <paramref name="to"/>; replaces an earlier hand-over of <paramref name="from"/>. Another instance's write between the read and the write is read again.</summary>
    public OwnerHandover Hand(string from, string to, string by, DateTimeOffset at)
    {
        var handover = new OwnerHandover(from, to, by, at);
        Change(all => all[from] = handover);
        return handover;
    }

    /// <summary>Ends the hand-over of <paramref name="from"/>: what carries its id is its own again. False when there was none.</summary>
    public bool Remove(string from)
    {
        var removed = false;
        Change(all => removed = all.Remove(from));
        return removed;
    }

    private void Change(Action<Dictionary<string, OwnerHandover>> change)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var current = settings.Get(Setting);
            var all = new Dictionary<string, OwnerHandover>(Parse(current), StringComparer.Ordinal);
            change(all);
            var text = Write(all);
            if (settings.Put(Setting, text, current) == text) return;
        }
        throw new InvalidOperationException("The hand-overs changed under this write ten times in a row; nothing was written.");
    }

    private static Dictionary<string, OwnerHandover> Parse(string? text)
    {
        var all = new Dictionary<string, OwnerHandover>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text)) return all;
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root) return all;
            foreach (var (from, node) in root)
                if (node is JsonObject o && (string?)o["to"] is { Length: > 0 } to)
                    all[from] = new OwnerHandover(from, to, (string?)o["by"] ?? "", o["at"] is JsonValue at && at.TryGetValue<DateTimeOffset>(out var when) ? when : DateTimeOffset.MinValue);
        }
        catch (JsonException) { /* a damaged setting hands nothing over: every owner stays what its definition says */ }
        return all;
    }

    private static string Write(Dictionary<string, OwnerHandover> all)
    {
        var root = new JsonObject();
        foreach (var h in all.Values.OrderBy(h => h.From, StringComparer.Ordinal)) root[h.From] = new JsonObject { ["to"] = h.To, ["by"] = h.By, ["at"] = h.At };
        return root.ToJsonString();
    }
}
