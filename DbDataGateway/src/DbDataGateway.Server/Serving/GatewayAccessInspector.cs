using System.Data.Common;
using System.Text.Json.Nodes;
using DbDataKit.Access;
using DbDataKit.Identity;
using DbDataKit.Secrets;
using DbDataGateway.Server.Secrets;
using DbDataKit.Semantic;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataKit.Store;
using DbDataGateway.Server.Configuration;

namespace DbDataGateway.Server.Serving;

/// <summary>A question to the access inspector that has an answer in words: the management route turns it into a problem with this status.</summary>
public sealed class AccessInspectorException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// What the Gateway tells about its access rules (plan 22a GP15): the rules of each served semantic model and entity model with the state of their rules tables, <c>access explain</c> (why a person or group can or cannot read a dataset or a row)
/// and <c>access coverage</c> (the values no rule row covers). Everything is answered by the one resolver and the one row predicate the serving core uses (<see cref="GatewayAccess.Resolver"/>, <see cref="StoreEntityAccess.Resolver"/>);
/// this class only finds the definition, the person and the rows of the data, so the explanation cannot differ from what a read does.
/// </summary>
public sealed class GatewayAccessInspector(GatewayAccess semantic, Func<SemanticModels> models, PublishedEntityModels entities, StateDatabase state, SecretsRuntime secrets, Func<GatewayOptions> options)
{
    public const string SemanticKind = GatewayCachePolicy.ModelPrefix;
    public const string EntityKind = "entity-model/";

    private sealed record Target(string Reference, string Name, AccessSection Section, SemanticModel? Model, ServedEntityModel? Served);

    /// <summary>Every served definition that has an <c>access</c> section.</summary>
    private List<Target> Targets()
    {
        var found = new List<Target>();
        foreach (var model in models().All.OrderBy(m => m.Name, StringComparer.Ordinal))
            if (model.Catalog.Access is { IsEmpty: false } section) found.Add(new Target(SemanticKind + model.Name, model.Name, section, model, null));
        foreach (var served in entities.All.OrderBy(m => m.Model.Name, StringComparer.Ordinal))
            if (served.Model.Access is { IsEmpty: false } section) found.Add(new Target(EntityKind + served.Model.Name, served.Model.Name, section, null, served));
        return found;
    }

    private Target Find(string reference)
    {
        var all = Targets();
        var exact = all.Where(t => string.Equals(t.Reference, reference, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        var named = all.Where(t => string.Equals(t.Name, reference, StringComparison.OrdinalIgnoreCase)).ToList();
        if (named.Count == 1) return named[0];
        if (named.Count > 1) throw new AccessInspectorException(409, $"'{reference}' is both a semantic model and an entity model with access rules; say {SemanticKind}{reference} or {EntityKind}{reference}.");
        throw new AccessInspectorException(404, $"This Gateway serves no model or entity model named '{reference}' that has access rules. Those it does: {(all.Count == 0 ? "none" : string.Join(", ", all.Select(t => t.Reference)))}.");
    }

    private sealed class NoSubjects : IRulesSubjects
    {
        public IReadOnlyCollection<string> Names(string table, string column) => throw new InvalidOperationException("the names of a rules table are not read for an overview");
    }

    /// <summary>The state of each rules table of a definition, without asking about anyone (the ledger is told the table was seen, as a read of the model does).</summary>
    private IReadOnlyList<RulesTableStatus> StatusOf(Target target)
    {
        var nobody = new AccessCaller("", "", []);
        var decision = target.Model is not null
            ? semantic.Resolver.Resolve(nobody, semantic.ScopeFor(target.Model, target.Section) with { Subjects = new NoSubjects() })
            : StoreEntityAccess.For(state).Resolver.Resolve(nobody, StoreEntityAccess.ScopeFor(target.Served!, target.Section, new NoSubjects()));
        return decision.Tables;
    }

    private static string Seconds(TimeSpan span) => ((long)span.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static JsonArray Warnings(Target target) => AccessPublishNotes.Warnings(target.Section, readOnDataSource: target.Model is not null, target.Model?.Catalog) ?? [];

    /// <summary>
    /// The definitions that have rules. <paramref name="full"/> (administrators) adds the rules themselves, the state of each rules table and the warnings; without it only the names and the kinds, so a person can ask about a model.
    /// </summary>
    public JsonObject Rules(bool full)
    {
        var list = new JsonArray();
        foreach (var target in Targets())
        {
            var item = new JsonObject
            {
                ["reference"] = target.Reference,
                ["name"] = target.Name,
                ["kind"] = target.Model is not null ? "semantic-model" : "entity-model",
                ["version"] = target.Model?.Version ?? target.Served?.Version,
                ["has_row_rules"] = target.Section.Row.Count > 0,
                ["has_column_rules"] = target.Section.Columns.Count > 0,
            };
            if (full)
            {
                item["max_age_seconds"] = Seconds(target.Section.MaxAge);
                item["row"] = new JsonArray([.. target.Section.Row.Select(Row)]);
                item["columns"] = new JsonArray([.. target.Section.Columns.Select(c => (JsonNode)new JsonObject
                {
                    ["subject"] = c.Subject, ["subject_kind"] = Lower(c.SubjectKind), ["dataset"] = c.Dataset, ["column"] = c.Column, ["treatment"] = Lower(c.Treatment), ["mask"] = c.Mask is { } m ? Lower(m) : null,
                })]);
                item["tables"] = new JsonArray([.. StatusOf(target).Select(s => (JsonNode)AccessJson.Table(new TableExplanation(s.Table, s.Key, s.State, s.Age, s.MaxAge, s.Since, s.ChangedAt)))]);
                item["warnings"] = Warnings(target);
                item["limits_reads_only"] = target.Served is not null && target.Section.Row.Count > 0;
            }

            list.Add(item);
        }

        return new JsonObject { ["definitions"] = list };
    }

    private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();

    private static JsonNode Row(DatasetRowRules d) => new JsonObject
    {
        ["dataset"] = d.Dataset, ["match"] = Lower(d.Match),
        ["groups"] = new JsonArray([.. d.Groups.Select(g => (JsonNode)new JsonObject
        {
            ["id"] = g.Id, ["match"] = Lower(g.Match),
            ["rules"] = new JsonArray([.. g.Rules.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id, ["effect"] = Lower(r.Effect), ["table"] = r.Table, ["subject"] = r.Subject, ["subject_kind"] = Lower(r.SubjectKind),
                ["columns"] = new JsonArray([.. r.Columns.Select(c => (JsonNode)new JsonObject { ["dataset_column"] = c.DatasetColumn, ["rules_column"] = c.RulesColumn })]),
                ["max_age_seconds"] = r.MaxAge is { } age ? Seconds(age) : null,
            })]),
        })]),
    };

    /// <summary>Whether the name is the person making a management call: their id or their name, compared ignoring case (a person may always ask about themselves).</summary>
    public static bool IsPerson(string subject, string subjectId, string? subjectName) =>
        string.Equals(subject, subjectId, StringComparison.OrdinalIgnoreCase) || (subjectName is not null && string.Equals(subject, subjectName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The caller the rules see for a person (their id, name and the groups of the identity home, found as the sign-in finds them: by id, display name or email), or for a group (its name as the only group). A name the home does not know is explained as
    /// it stands, with a note: a rule that names it directly can still match.
    /// </summary>
    public (AccessCaller Caller, SubjectKind Kind, List<string> Notes) Subject(string subject, string? kind)
    {
        if (string.IsNullOrWhiteSpace(subject)) throw new AccessInspectorException(400, "Say whose access to explain: a person or a group.");
        var notes = new List<string>();
        var wanted = kind?.ToLowerInvariant();
        if (wanted is not (null or "person" or "group")) throw new AccessInspectorException(400, "The kind of a subject is `person` or `group`.");
        var directory = new HomeDirectory(state);
        if (wanted != "group")
        {
            var user = new IdentityUserStore(state).List().FirstOrDefault(u => string.Equals(u.Id, subject, StringComparison.OrdinalIgnoreCase) || string.Equals(u.DisplayName, subject, StringComparison.OrdinalIgnoreCase)
                || (u.Email is not null && string.Equals(u.Email, subject, StringComparison.OrdinalIgnoreCase)));
            if (user is not null) return (new AccessCaller(user.Id, user.DisplayName, [.. directory.GroupsOf(user.Id).Select(g => g.Name)]), SubjectKind.Person, notes);
            if (wanted == "person" || !directory.Groups().Any(g => string.Equals(g.Name, subject, StringComparison.OrdinalIgnoreCase)))
            {
                notes.Add(wanted == "person"
                    ? $"The identity home knows no person named '{subject}', so only a rule that names them directly can match, and they are in no group."
                    : $"The identity home knows no person or group named '{subject}': explained as a person by that name, in no group.");
                return (new AccessCaller(subject, subject, []), SubjectKind.Person, notes);
            }
        }

        var group = directory.Groups().FirstOrDefault(g => string.Equals(g.Name, subject, StringComparison.OrdinalIgnoreCase));
        if (group is null) notes.Add($"The identity home has no group named '{subject}'; the rules are matched with that name as it stands.");
        return (new AccessCaller("", "", [group?.Name ?? subject]), SubjectKind.Group, notes);
    }

    /// <summary>The row asked about, from the arguments <c>column=value</c> (<c>column=null</c> is NULL; quote a value to keep a literal text: <c>column="null"</c>).</summary>
    public static IReadOnlyDictionary<string, string?>? ParseRow(IEnumerable<string> arguments)
    {
        var row = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var argument in arguments)
        {
            var at = argument.IndexOf('=');
            if (at <= 0) throw new AccessInspectorException(400, $"A row is given as column=value; '{argument}' is not.");
            var column = argument[..at].Trim();
            var value = argument[(at + 1)..];
            if (value.Length >= 2 && (value[0] == '"' && value[^1] == '"' || value[0] == '\'' && value[^1] == '\'')) value = value[1..^1];
            else if (value.Equals("null", StringComparison.OrdinalIgnoreCase)) { row[column] = null; continue; }
            row[column] = value;
        }

        return row.Count == 0 ? null : row;
    }

    /// <summary>Explains a person or group on a definition (and a dataset, and a row of it) by the one resolver and predicate; see <see cref="AccessExplainer"/>.</summary>
    public JsonObject Explain(string subject, string? kind, string definition, string? dataset, IReadOnlyDictionary<string, string?>? row)
    {
        var target = Find(definition);
        var (caller, subjectKind, notes) = Subject(subject, kind);
        CheckDataset(target, dataset, row);
        var warnings = new List<string>(notes);
        if (target.Served is not null && target.Section.Row.Count > 0) warnings.Add(AccessPublishNotes.EntityWritesWarning);
        if (target.Model is not null) warnings.AddRange(CatalogColumnView.MasksThatHide(target.Model.Catalog, target.Section).Select(m => AccessPublishNotes.MaskHidesWarning(m.Dataset, m.Column, m.Reason)));
        return Run(target, async (reader, resolver, scope) =>
            new AccessExplainer(resolver, reader).Explain(caller, scope, dataset, row, warnings, subject, subjectKind).ToJson()).Result;
    }

    /// <summary>The values of the filtered datasets that no rule row covers, per rule.</summary>
    public JsonObject Coverage(string definition, string? dataset)
    {
        var target = Find(definition);
        CheckDataset(target, dataset, null);
        return Run(target, (reader, _, _) => Task.FromResult(new JsonObject
        {
            ["definition"] = target.Reference,
            ["rules"] = new JsonArray([.. AccessCoverage.Report(target.Section, reader, dataset).Select(r => (JsonNode)r.ToJson())]),
        })).Result;
    }

    private static void CheckDataset(Target target, string? dataset, IReadOnlyDictionary<string, string?>? row)
    {
        if (dataset is null)
        {
            if (row is not null) throw new AccessInspectorException(400, "A row is asked about one dataset: say which with --dataset.");
            return;
        }

        var known = target.Model is not null
            ? (target.Model.Catalog.Source["tables"] as JsonArray)?.OfType<JsonObject>().Select(t => t["name"]?.GetValue<string>()).Contains(dataset) == true
            : target.Served!.Model.Entity(dataset) is not null;
        if (!known) throw new AccessInspectorException(404, $"{target.Reference} has no {(target.Model is not null ? "table" : "entity")} '{dataset}'.");
    }

    /// <summary>Runs work with the reader and the resolver of the definition: a semantic model reads through the data source; an entity of a Store model through a writer on its own database.</summary>
    private async Task<T> Run<T>(Target target, Func<IRulesTableReader, AccessResolver, AccessScope, Task<T>> work)
    {
        if (target.Model is not null)
            return await work(new SemanticRulesReader(semantic, target.Model), semantic.Resolver, semantic.ScopeFor(target.Model, target.Section)).ConfigureAwait(false);
        var served = target.Served!;
        await using var connection = await EntityConnection(served).ConfigureAwait(false);
        var writer = new ModelWriter(served.Model, connection.Connection, connection.Engine, schema: served.Model.Name);
        return await work(new EntityRulesReader(writer), StoreEntityAccess.For(state).Resolver, StoreEntityAccess.ScopeFor(served, target.Section, new WriterSubjects(writer))).ConfigureAwait(false);
    }

    private sealed class WriterSubjects(ModelWriter writer) : IRulesSubjects
    {
        public IReadOnlyCollection<string> Names(string table, string column) => writer.RulesSubjects(table, column);
    }

    private sealed class OpenConnection(DbConnection connection, string engine) : IAsyncDisposable
    {
        public DbConnection Connection { get; } = connection;
        public string Engine { get; } = engine;

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    /// <summary>The database of a Store model's tables (the data source published with it), opened as the Store's group routes open it.</summary>
    private async Task<OpenConnection> EntityConnection(ServedEntityModel served)
    {
        var current = options();
        if (!current.DataSources.TryGetValue(served.DataSource, out var source)) throw new AccessInspectorException(502, $"The data source '{served.DataSource}' the model's tables are on is not served here.");
        var engine = source.Provider.ToLowerInvariant() switch { "sqlserver" => "sqlserver", "postgres" => "postgres", _ => null };
        if (engine is null) throw new AccessInspectorException(502, $"The data source '{served.DataSource}' is {source.Provider}; a model's tables are on SQL Server or PostgreSQL.");
        string text;
        try { text = secrets.ResolveConnectionString($"Gateway:DataSources:{served.DataSource}:ConnectionString", source.ConnectionString, source.Provider) ?? source.ConnectionString; }
        catch (Exception ex) when (ex is SecretNotFoundException or InvalidOperationException) { throw new AccessInspectorException(502, $"The login of the data source '{served.DataSource}' cannot be found here."); }
        DbConnection connection = engine == "sqlserver" ? new Microsoft.Data.SqlClient.SqlConnection(text) : new Npgsql.NpgsqlConnection(text);
        try { await connection.OpenAsync().ConfigureAwait(false); }
        catch (DbException) { await connection.DisposeAsync().ConfigureAwait(false); throw new AccessInspectorException(502, $"The database of the data source '{served.DataSource}' could not be reached."); }
        return new OpenConnection(connection, engine);
    }
}
