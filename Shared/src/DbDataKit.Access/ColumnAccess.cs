using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.Access;

/// <summary>
/// What one caller sees of one column (plan 22a GP13). The treatments are ordered by strength: <see cref="ColumnTreatment.Hide"/> beats <see cref="ColumnTreatment.Null"/> beats <see cref="ColumnTreatment.Mask"/>,
/// and when several rules match a caller the strongest wins (a person in two groups that treat the same column differently gets the stricter one).
/// </summary>
public sealed record ColumnTreatmentOf(ColumnTreatment Treatment, MaskKind Mask = MaskKind.Fixed)
{
    /// <summary>The stronger of two treatments of one column; for two masks of different kinds the fixed one (it reveals less than a hash, which groups).</summary>
    public static ColumnTreatmentOf Stronger(ColumnTreatmentOf a, ColumnTreatmentOf b)
    {
        var rank = Rank(a.Treatment) - Rank(b.Treatment);
        if (rank != 0) return rank > 0 ? a : b;
        return a.Treatment == ColumnTreatment.Mask && a.Mask != b.Mask ? new ColumnTreatmentOf(ColumnTreatment.Mask, MaskKind.Fixed) : a;
    }

    private static int Rank(ColumnTreatment treatment) => treatment switch { ColumnTreatment.Hide => 3, ColumnTreatment.Null => 2, _ => 1 };

    /// <summary>The text of the treatment as the fingerprint and the catalog's properties write it: <c>hide</c>, <c>null</c>, <c>mask:fixed</c>, <c>mask:hash</c>.</summary>
    public override string ToString() => Treatment == ColumnTreatment.Mask ? "mask:" + Mask.ToString().ToLowerInvariant() : Treatment.ToString().ToLowerInvariant();
}

/// <summary>
/// The column rules of an <c>access</c> section resolved for one caller: for each (dataset, column) the strongest treatment of the rules whose subject is the caller (by name, for a person) or one of the caller's groups.
/// A caller no rule names gets an empty set and sees every column as it is. The <see cref="Fingerprint"/> is the part of the cache key that column rules own (a cached answer made for one set of treatments is never served to
/// a caller with another); the resolver of plan 22a GP10 folds it into its own fingerprint.
/// </summary>
public sealed class ColumnTreatments
{
    public static readonly ColumnTreatments None = new(new Dictionary<(string, string), ColumnTreatmentOf>());

    private readonly Dictionary<(string Dataset, string Column), ColumnTreatmentOf> _byColumn;

    private ColumnTreatments(Dictionary<(string, string), ColumnTreatmentOf> byColumn)
    {
        _byColumn = byColumn;
        Fingerprint = byColumn.Count == 0
            ? "-"
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', byColumn.OrderBy(e => e.Key.Item1, StringComparer.Ordinal).ThenBy(e => e.Key.Item2, StringComparer.Ordinal)
                .Select(e => $"{e.Key.Item1}.{e.Key.Item2}={e.Value}")))))[..16].ToLowerInvariant();
    }

    /// <summary>True when no column is treated: the caller sees everything as it is.</summary>
    public bool IsEmpty => _byColumn.Count == 0;

    /// <summary>A short hash of the treatments (<c>-</c> for none); equal for two callers whose treatments are equal, so they share a cache entry.</summary>
    public string Fingerprint { get; }

    /// <summary>The treatment of a column of a dataset, or null when the caller sees it as it is.</summary>
    public ColumnTreatmentOf? Of(string dataset, string column) => _byColumn.TryGetValue((dataset, column), out var treatment) ? treatment : null;

    public IEnumerable<(string Dataset, string Column, ColumnTreatmentOf Treatment)> All => _byColumn.Select(e => (e.Key.Dataset, e.Key.Column, e.Value));

    /// <summary>
    /// Resolves the section's column rules for a caller. A <c>person</c> rule names the caller's id or name; a <c>group</c> rule names one of the groups (all compared ignoring case). The strongest treatment wins
    /// when more than one rule matches the same column.
    /// </summary>
    public static ColumnTreatments For(AccessSection? section, string id, string name, IEnumerable<string> groups)
    {
        if (section is not { Columns.Count: > 0 }) return None;
        var people = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id, name };
        var home = new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);
        var found = new Dictionary<(string, string), ColumnTreatmentOf>();
        foreach (var rule in section.Columns)
        {
            if (!(rule.SubjectKind == SubjectKind.Person ? people : home).Contains(rule.Subject)) continue;
            var treatment = new ColumnTreatmentOf(rule.Treatment, rule.Mask ?? MaskKind.Fixed);
            var key = (rule.Dataset, rule.Column);
            found[key] = found.TryGetValue(key, out var known) ? ColumnTreatmentOf.Stronger(known, treatment) : treatment;
        }

        return found.Count == 0 ? None : new ColumnTreatments(found);
    }
}

/// <summary>
/// The values a mask returns. <see cref="FixedValue"/> is one constant for every value (so the length of the real value is not shown); <see cref="Hash"/> is HMAC-SHA256 under the install's key over the value, per column, cut to 16 hex
/// digits: the same value in the same column always gives the same mask (a group-by on it still groups), a mask cannot be turned back into the value, and the same value in another column gives another mask, so two masked columns
/// cannot be matched against each other.
/// </summary>
public static class ColumnMasks
{
    public const string FixedValue = "********";

    public static string Hash(byte[] key, string dataset, string column, string value) =>
        "#" + Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(dataset + "\u001f" + column + "\u001f" + value)))[..16].ToLowerInvariant();
}
