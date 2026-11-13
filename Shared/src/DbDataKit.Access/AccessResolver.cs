using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbDataKit.Access;

/// <summary>Who asks, as the access rules see them: the id and the name (a person is matched by either) and the groups of the identity home (a group is matched by any of them). Names compare ignoring case.</summary>
public sealed record AccessCaller(string Id, string Name, IReadOnlyCollection<string> Groups)
{
    /// <summary>The names a rule of <paramref name="kind"/> can match: a person's id and name, or the caller's groups.</summary>
    public IEnumerable<string> NamesOf(SubjectKind kind) => kind == SubjectKind.Person ? new[] { Id, Name }.Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.OrdinalIgnoreCase) : Groups;
}

/// <summary>How fresh a rules table is (plan 22a GP10, QG2): <c>Fresh</c> is within half its maximum age; <c>Stale</c> is past half (the last good rules are served and an alert is raised once); <c>Expired</c> is past the maximum (a person with a rule is refused).</summary>
public enum RulesTableState { Fresh, Stale, Expired }

/// <summary>What the group knows of a rules table: when it was first served and when the last <c>data.changed</c> for it arrived (its refresh generation).</summary>
/// <param name="Key">The table's identity in the state (the host chooses it; equal for one table however many definitions name it).</param>
/// <param name="FirstSeen">The day the table was first served on the group: a table never refreshed counts its age from then.</param>
/// <param name="ChangedAt">When the producer last reported the table refreshed; null when it never did.</param>
public sealed record RulesTableRecord(string Key, DateTimeOffset FirstSeen, DateTimeOffset? ChangedAt)
{
    /// <summary>The instant the age counts from, and the number of the table's generation.</summary>
    public DateTimeOffset Since => ChangedAt ?? FirstSeen;
}

/// <summary>The group's record of the rules tables (a module of the serving state: it survives a restart and every instance reads the same).</summary>
public interface IRulesTableLedger
{
    /// <summary>The record of the table, made with <paramref name="now"/> as its first sight when the group has none.</summary>
    RulesTableRecord Touch(string key, DateTimeOffset now);

    /// <summary>Notes that the producer refreshed the table at <paramref name="at"/> (never moves the time back).</summary>
    void Changed(string key, DateTimeOffset at, string product, string reason);
}

/// <summary>Reads the names a rules table holds in a subject column (distinct, as spelled in the table). The resolver asks once per generation of a table and keeps the answer.</summary>
public interface IRulesSubjects
{
    /// <summary>The distinct values of <paramref name="column"/> of the rules table the section calls <paramref name="table"/>. Throws when the table cannot be read; the dataset is then refused (plan 22a D2).</summary>
    IReadOnlyCollection<string> Names(string table, string column);
}

/// <summary>Where the resolver raises its alerts. Until the notification delivery is wired (plan 22a GP16) a host writes an audit entry and a log line.</summary>
public interface IAccessAlerts
{
    /// <summary>A rules table passed half its maximum age (raised once until it is refreshed).</summary>
    void RulesTableStale(RulesTableStatus status);
}

/// <summary>An alert port that does nothing.</summary>
public sealed class NoAccessAlerts : IAccessAlerts
{
    public static NoAccessAlerts Instance { get; } = new();

    public void RulesTableStale(RulesTableStatus status) { }
}

/// <summary>A rules table of a section at one moment.</summary>
/// <param name="Table">The name the section calls it by.</param>
/// <param name="Key">Its identity in the state.</param>
/// <param name="Age">The time since its last refresh (or since it was first served).</param>
/// <param name="State">Fresh, stale or expired by its age.</param>
/// <param name="MaxAge">The longest age it may have (the smallest of the rules that name it).</param>
/// <param name="Since">The instant its age counts from.</param>
/// <param name="ChangedAt">When the producer last reported it refreshed (<c>data.changed</c>); null when it never did, and its age counts from when it was first served.</param>
public sealed record RulesTableStatus(string Table, string Key, RulesTableState State, TimeSpan Age, TimeSpan MaxAge, DateTimeOffset Since, DateTimeOffset? ChangedAt = null);

/// <summary>A rule with the caller's names that appear in its rules table, spelled as the table spells them; empty when the table has no row for the caller, so the rule cannot hold for any row.</summary>
public sealed record RuleResolution(RowRule Rule, IReadOnlyList<string> Names);

public sealed record GroupResolution(RuleGroup Group, IReadOnlyList<RuleResolution> Rules);

/// <summary>The row rules of one dataset as they apply to one caller. <see cref="Refusal"/> is set when a rules table of the dataset is expired (or cannot be read) for a caller who has rules in it: the dataset is not served to that caller (D2).</summary>
public sealed record DatasetResolution(DatasetRowRules Rules, IReadOnlyList<GroupResolution> Groups, string? Refusal)
{
    public string Dataset => Rules.Dataset;
}

/// <summary>
/// What applies to a caller on a definition: the row rules per dataset with the caller's names in each rules table, the column rules that name the caller, the state of each rules table and a <see cref="Fingerprint"/> of all of it.
/// Two callers with the same effective rules have the same fingerprint, and a refresh of a rules table changes it.
/// </summary>
public sealed record AccessDecision(string Fingerprint, IReadOnlyList<DatasetResolution> Datasets, IReadOnlyList<ColumnRule> Columns, IReadOnlyList<RulesTableStatus> Tables)
{
    /// <summary>The resolution of a dataset, or null when it has no row rules.</summary>
    public DatasetResolution? DatasetOf(string dataset) => Datasets.FirstOrDefault(d => d.Dataset == dataset);

    /// <summary>The first refusal of any dataset, or null.</summary>
    public string? AnyRefusal => Datasets.Select(d => d.Refusal).FirstOrDefault(r => r is not null);

    /// <summary>The decision of a definition that has no access section: nothing applies and every caller has the same fingerprint.</summary>
    public static AccessDecision None { get; } = new("none", [], [], []);
}

/// <summary>What the resolver is asked about: a definition's section with the host's name for each of its rules tables and the way to read its subjects.</summary>
/// <param name="Definition">The definition's identity (<c>semantic-model/sales</c>), for messages.</param>
/// <param name="Section">The <c>access</c> section.</param>
/// <param name="TableKey">The identity in the state of a rules table the section names (the same table gives the same key in every definition).</param>
/// <param name="Subjects">Reads the names of a rules table's subject column.</param>
public sealed record AccessScope(string Definition, AccessSection Section, Func<string, string> TableKey, IRulesSubjects Subjects);

/// <summary>
/// The one place that knows what applies to a caller (plan 22a GP10, QG2): the serving core asks it for the filter, the cache for the fingerprint and <c>access explain</c> for the explanation. It reads the rules table's names once per generation of
/// the table (a refresh is a new generation) and keeps them; nothing else about a rules table is kept here, and no row of one ever is.
/// </summary>
public sealed class AccessResolver(IRulesTableLedger ledger, IAccessAlerts? alerts = null, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly IAccessAlerts _alerts = alerts ?? NoAccessAlerts.Instance;
    private readonly object _gate = new();
    private readonly Dictionary<string, IReadOnlyCollection<string>> _names = new(StringComparer.Ordinal);
    private readonly HashSet<string> _alerted = new(StringComparer.Ordinal);

    /// <summary>The access decision for <paramref name="caller"/> on a definition. A section with no rule at all gives <see cref="AccessDecision.None"/>.</summary>
    public AccessDecision Resolve(AccessCaller caller, AccessScope scope)
    {
        var section = scope.Section;
        if (section.IsEmpty) return AccessDecision.None;
        var now = _time.GetUtcNow();
        var statuses = new List<RulesTableStatus>();
        var records = new Dictionary<string, RulesTableRecord>(StringComparer.Ordinal);
        foreach (var (table, maxAge) in section.RulesTables().OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            var key = scope.TableKey(table);
            var record = ledger.Touch(key, now);
            records[table] = record;
            var age = now - record.Since;
            if (age < TimeSpan.Zero) age = TimeSpan.Zero;
            var state = age > maxAge ? RulesTableState.Expired : age > maxAge / 2 ? RulesTableState.Stale : RulesTableState.Fresh;
            var status = new RulesTableStatus(table, key, state, age, maxAge, record.Since, record.ChangedAt);
            statuses.Add(status);
            Alert(status);
        }

        var datasets = new List<DatasetResolution>();
        foreach (var rules in section.Row.OrderBy(r => r.Dataset, StringComparer.Ordinal))
        {
            string? refusal = null;
            var groups = new List<GroupResolution>();
            foreach (var group in rules.Groups)
            {
                var resolved = new List<RuleResolution>();
                foreach (var rule in group.Rules)
                {
                    IReadOnlyList<string> names;
                    try { names = NamesIn(scope, rule, records[rule.Table], caller); }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                    {
                        // an unreadable rules table is a refusal, never an open door (D2)
                        refusal ??= $"The rules table '{rule.Table}' of {scope.Definition} cannot be read ({ex.GetType().Name}), so '{rules.Dataset}' is not served.";
                        names = [];
                    }

                    resolved.Add(new RuleResolution(rule, names));
                    var status = statuses.First(s => s.Table == rule.Table);
                    if (status.State == RulesTableState.Expired && names.Count > 0)
                        refusal ??= $"The rules table '{rule.Table}' of {scope.Definition} was last refreshed {Describe(status.Age)} ago, past its maximum age of {Describe(status.MaxAge)}, so '{rules.Dataset}' is not served to anyone it has rules for.";
                }

                groups.Add(new GroupResolution(group, resolved));
            }

            datasets.Add(new DatasetResolution(rules, groups, refusal));
        }

        var columns = section.Columns.Where(c => caller.NamesOf(c.SubjectKind).Contains(c.Subject, StringComparer.OrdinalIgnoreCase)).ToList();
        return new AccessDecision(Fingerprint(section, statuses, datasets, columns), datasets, columns, statuses);
    }

    private void Alert(RulesTableStatus status)
    {
        bool raise;
        lock (_gate)
        {
            if (status.State == RulesTableState.Fresh) { _alerted.RemoveWhere(a => a.StartsWith(status.Key + "\n", StringComparison.Ordinal)); return; }
            // once per table and generation while it stays stale or expired, however many requests follow
            raise = status.State == RulesTableState.Stale && _alerted.Add(status.Key + "\n" + status.Since.UtcTicks);
        }

        if (raise) _alerts.RulesTableStale(status);
    }

    private IReadOnlyList<string> NamesIn(AccessScope scope, RowRule rule, RulesTableRecord record, AccessCaller caller)
    {
        var all = SubjectsOf(scope, rule, record);
        var wanted = caller.NamesOf(rule.SubjectKind).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return [.. all.Where(wanted.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    private IReadOnlyCollection<string> SubjectsOf(AccessScope scope, RowRule rule, RulesTableRecord record)
    {
        var cacheKey = record.Key + "\n" + rule.Subject + "\n" + record.Since.UtcTicks;
        lock (_gate)
            if (_names.TryGetValue(cacheKey, out var known)) return known;
        var read = scope.Subjects.Names(rule.Table, rule.Subject);
        lock (_gate)
        {
            // a generation that has gone is never asked for again
            foreach (var old in _names.Keys.Where(k => k.StartsWith(record.Key + "\n" + rule.Subject + "\n", StringComparison.Ordinal)).ToList()) _names.Remove(old);
            _names[cacheKey] = read;
        }

        return read;
    }

    private static readonly JsonSerializerOptions Canonical = new() { WriteIndented = false, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static string Fingerprint(AccessSection section, List<RulesTableStatus> tables, List<DatasetResolution> datasets, List<ColumnRule> columns)
    {
        var text = new StringBuilder("access-fingerprint/1\n");
        text.Append(JsonSerializer.Serialize(section, Canonical)).Append('\n');
        foreach (var table in tables) text.Append("generation ").Append(table.Key).Append(' ').Append(table.Since.UtcTicks).Append('\n');
        foreach (var dataset in datasets)
            foreach (var group in dataset.Groups)
                foreach (var rule in group.Rules)
                    text.Append("names ").Append(dataset.Dataset).Append('/').Append(group.Group.Id).Append('/').Append(rule.Rule.Id).Append(": ").AppendJoin('|', rule.Names.Select(n => n.ToLowerInvariant()).Order(StringComparer.Ordinal)).Append('\n');
        foreach (var column in columns.OrderBy(c => c.Dataset, StringComparer.Ordinal).ThenBy(c => c.Column, StringComparer.Ordinal))
            text.Append("column ").Append(column.Dataset).Append('.').Append(column.Column).Append(' ').Append(column.Treatment).Append(' ').Append(column.Mask).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    private static string Describe(TimeSpan span) => span.TotalDays >= 1 ? $"{span.TotalDays:0.#} days" : span.TotalHours >= 1 ? $"{span.TotalHours:0.#} hours" : span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0.#} minutes" : $"{span.TotalSeconds:0} seconds";
}
