using System.Text.Json.Nodes;
using DbDataKit.Access;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// The physical tables the served models put access rules on, per data source (plan 22a GP12, QG3): the ONE source of truth for the pass-through refusal and the catalog listing. A semantic model's table is ruled when its <c>access:</c> section has row rules or
/// column rules for it; an entity of a published Store model is ruled the same way, and its table is the entity's name in the model's schema (<c>model</c> or <c>model__instance</c>) on the model's data source. The index is built from the models as they are
/// served now and is kept until the set of served definitions changes (a publish, a rollback or a retire replaces the model object, so the next statement rebuilds it): nothing here is refreshed by a timer or by hand.
/// <para>
/// A rules table (the table a rule reads) is not ruled by that: reading it is what the data source's own grants (D9) decide. A data source no model puts rules on is untouched.
/// </para>
/// </summary>
public sealed class RuledTables(Func<SemanticModels> models, Func<IReadOnlyCollection<ServedEntityModel>> entityModels)
{
    private sealed record Built(object[] Key, IReadOnlyDictionary<string, IReadOnlyList<Ruled>> Index);

    private volatile Built? _built;

    /// <summary>One ruled physical table.</summary>
    /// <param name="Source">The serving data source it is on.</param>
    /// <param name="Table">The table as the model writes it (<c>dbo.orders</c>).</param>
    /// <param name="Name">The last part of its name, the part a statement must name.</param>
    /// <param name="Schema">For an entity of a Store model, the model's schema (the table is in it or in <c>Schema__instance</c>); null for a semantic table, whose qualifier is not compared.</param>
    /// <param name="Holder">The model that holds the rules, as a person names it (<c>the semantic model 'semantic:orders_model'</c>).</param>
    /// <param name="Rules">The rules, for the message (<c>row rules 'east_only'; column rules on 'email'</c>).</param>
    /// <param name="Ask">How to get the same data through the model.</param>
    /// <param name="ModelName">The name of the model that holds the rules (<c>orders_model</c>), for the publish warning.</param>
    public sealed record Ruled(string Source, string Table, string Name, string? Schema, string Holder, string Rules, string Ask, string ModelName = "")
    {
        /// <summary>Whether the name at <paramref name="index"/> of a dotted chain could be this table: the same name (any case), and for a Store table the part before it, when there is one, is the model's schema.</summary>
        public bool MatchesAt(IReadOnlyList<string> chain, int index)
        {
            if (!string.Equals(chain[index].Trim(), Name, StringComparison.OrdinalIgnoreCase)) return false;
            if (Schema is null || index == 0) return true;
            var qualifier = chain[index - 1].Trim();
            return string.Equals(qualifier, Schema, StringComparison.OrdinalIgnoreCase) || qualifier.StartsWith(Schema + "__", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Why a statement or listing entry is refused: the ruled tables it names, and the message.</summary>
    public sealed record Refusal(IReadOnlyList<Ruled> Tables, string Message);

    /// <summary>The ruled tables of a data source (the list is empty for a source nobody put rules on).</summary>
    public IReadOnlyList<Ruled> On(string source) => Index().TryGetValue(source, out var list) ? list : [];

    /// <summary>Whether a data source has any ruled table (the statement text is scanned only then).</summary>
    public bool Any(string source) => On(source).Count > 0;

    /// <summary>The refusal of a statement on a data source, or null when it names no ruled table.</summary>
    public Refusal? Check(string source, string sql)
    {
        var ruled = On(source);
        if (ruled.Count == 0) return null;
        var scan = TableNameScan.Scan(sql);
        if (scan.Opaque is { } why)
            return new Refusal(ruled, $"The statement uses {why}, which cannot be checked against the access rules on data source '{source}' ({Describe(ruled)}), so it is not run. Ask the model instead: {AskText(ruled)}.");
        var hits = new List<Ruled>();
        foreach (var chain in scan.Chains)
            for (var i = 0; i < chain.Count; i++)
                foreach (var table in ruled)
                    if (table.MatchesAt(chain, i) && !hits.Contains(table)) hits.Add(table);
        if (hits.Count == 0) return null;
        var first = hits[0];
        var message = hits.Count == 1
            ? $"The table '{first.Table}' of data source '{source}' has access rules ({first.Rules}) in {first.Holder}, so a statement that names it is not run. Ask the model instead: {first.Ask}."
            : $"The tables {string.Join(", ", hits.Select(h => $"'{h.Table}'"))} of data source '{source}' have access rules ({string.Join("; ", hits.Select(h => $"'{h.Table}': {h.Rules} in {h.Holder}"))}), so a statement that names them is not run. Ask the model instead: {AskText(hits)}.";
        return new Refusal(hits, message);
    }

    /// <summary>Whether the catalog entry at <paramref name="path"/> (catalog, schema, name) of a data source is a ruled table: it is then not listed for pass-through.</summary>
    public bool IsRuled(string source, IReadOnlyList<string> path)
    {
        if (path.Count == 0) return false;
        foreach (var table in On(source))
            if (table.MatchesAt(path, path.Count - 1)) return true;
        return false;
    }

    private static string Describe(IReadOnlyList<Ruled> ruled) => string.Join("; ", ruled.Select(r => $"'{r.Table}' in {r.Holder}").Distinct().Take(5));

    private static string AskText(IReadOnlyList<Ruled> ruled) => string.Join(" or ", ruled.Select(r => r.Ask).Distinct().Take(5));

    private IReadOnlyDictionary<string, IReadOnlyList<Ruled>> Index()
    {
        var semantic = models().All;
        var entities = entityModels();
        object[] key = [.. semantic, .. entities];
        if (_built is { } built && built.Key.Length == key.Length && built.Key.Zip(key).All(pair => ReferenceEquals(pair.First, pair.Second))) return built.Index;   // the same model objects: nothing was published, rolled back or retired since

        var found = new List<Ruled>();
        foreach (var model in semantic) found.AddRange(OfSemantic(model));
        foreach (var served in entities) found.AddRange(OfEntity(served));
        IReadOnlyDictionary<string, IReadOnlyList<Ruled>> index = found.GroupBy(r => r.Source, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Ruled>)[.. g], StringComparer.OrdinalIgnoreCase);
        _built = new Built(key, index);
        return index;
    }

    /// <summary>The ruled tables one semantic model has (what the index holds for it).</summary>
    internal static IReadOnlyList<Ruled> RuledBy(SemanticModel model) => [.. OfSemantic(model)];

    /// <summary>The ruled tables one entity model has on its data source.</summary>
    internal static IReadOnlyList<Ruled> RuledBy(ServedEntityModel served) => [.. OfEntity(served)];

    /// <summary>Every physical table a semantic model reads: its serving data source and the name chain of the table.</summary>
    internal static IReadOnlyList<(string Source, IReadOnlyList<string> Chain)> ReadsOf(SemanticModel model)
    {
        var reads = new List<(string, IReadOnlyList<string>)>();
        if (model.Catalog.Source["tables"] is not JsonArray tables) return reads;
        foreach (var table in tables.OfType<JsonObject>())
            if (table["source"]?.GetValue<string>() is { Length: > 0 } physical && TableNameScan.Scan(physical).Chains.FirstOrDefault() is { Count: > 0 } chain)
                reads.Add((model.ServingSource(table["x_source"]?.GetValue<string>() ?? ""), chain));
        return reads;
    }

    /// <summary>Every table an entity model reads: its entities, on its data source, in the schema of the model.</summary>
    internal static IReadOnlyList<(string Source, IReadOnlyList<string> Chain)> ReadsOf(ServedEntityModel served) =>
        [.. served.Model.Entities.Select(e => (served.DataSource, (IReadOnlyList<string>)[served.Model.Name, e.Name]))];

    /// <summary>
    /// The warnings of a publish whose model reads tables (owner decision 2026-11-12, plan 22a GP12): for each table it reads that ANOTHER served model puts rules on while this model puts none on it, one sentence naming that model and the table.
    /// A model that carries rules of its own for the table (any rules, even different ones) says nothing; a model that is no longer served says nothing, as the index is built from the models served now.
    /// </summary>
    /// <param name="holder">The holder text of the publishing model (<see cref="Ruled.Holder"/>): its own entries in the index are not another model's.</param>
    /// <param name="own">The tables the publishing model puts rules on.</param>
    /// <param name="reads">The tables the publishing model reads.</param>
    internal JsonArray? UnappliedWarnings(string holder, IReadOnlyList<Ruled> own, IReadOnlyList<(string Source, IReadOnlyList<string> Chain)> reads)
    {
        var notes = new List<string>();
        foreach (var (source, chain) in reads)
        {
            var last = chain.Count - 1;
            if (own.Any(o => string.Equals(o.Source, source, StringComparison.OrdinalIgnoreCase) && o.MatchesAt(chain, last))) continue;
            foreach (var other in On(source).Where(r => r.Holder != holder && r.MatchesAt(chain, last)))
            {
                var note = UnappliedWarning(other.ModelName, other.Table, source);
                if (!notes.Contains(note)) notes.Add(note);
            }
        }
        return notes.Count == 0 ? null : new JsonArray([.. notes.Select(n => (JsonNode)n)]);
    }

    /// <summary>The words of the warning: the model that holds the rules, the table, the data source.</summary>
    public static string UnappliedWarning(string model, string table, string source) => $"Model '{model}' puts access rules on '{table}' of data source '{source}'; this model reads the same table and does not apply them.";

    /// <summary>The holder text of a semantic model, as an entry of the index names it.</summary>
    internal static string HolderOf(SemanticModel model) => $"the semantic model '{SemanticModels.SourcePrefix + model.Name}'";

    /// <summary>The holder text of an entity model.</summary>
    internal static string HolderOf(ServedEntityModel served) => $"the entity model '{served.Model.Name}'";

    private static IEnumerable<Ruled> OfSemantic(SemanticModel model)
    {
        if (model.Catalog.Access is not { IsEmpty: false } section || model.Catalog.Source["tables"] is not JsonArray tables) yield break;
        var holder = HolderOf(model);
        var ask = $"read it from data source '{SemanticModels.SourcePrefix + model.Name}' (a semantic request or semantic SQL), which applies the rules";
        foreach (var dataset in section.Row.Select(r => r.Dataset).Concat(section.Columns.Select(c => c.Dataset)).Distinct(StringComparer.Ordinal))
        {
            var table = tables.OfType<JsonObject>().FirstOrDefault(t => t["name"]?.GetValue<string>() == dataset);
            if (table?["source"]?.GetValue<string>() is not { Length: > 0 } physical) continue;
            var chain = TableNameScan.Scan(physical).Chains.FirstOrDefault();
            if (chain is null || chain.Count == 0) continue;
            var serving = model.ServingSource(table["x_source"]?.GetValue<string>() ?? "");
            yield return new Ruled(serving, string.Join('.', chain), chain[^1], null, holder, RulesOf(section, dataset), ask, model.Name);
        }
    }

    private static IEnumerable<Ruled> OfEntity(ServedEntityModel served)
    {
        if (served.Model.Access is not { IsEmpty: false } section) yield break;
        var holder = HolderOf(served);
        var ask = $"read the entity through the Store (the model '{served.Model.Name}'), which applies the rules";
        foreach (var dataset in section.Row.Select(r => r.Dataset).Concat(section.Columns.Select(c => c.Dataset)).Distinct(StringComparer.Ordinal))
        {
            if (served.Model.Entity(dataset) is null) continue;
            yield return new Ruled(served.DataSource, $"{served.Model.Name}.{dataset}", dataset, served.Model.Name, holder, RulesOf(section, dataset), ask, served.Model.Name);
        }
    }

    private static string RulesOf(AccessSection section, string dataset)
    {
        var parts = new List<string>();
        if (section.RowRulesOf(dataset) is { } row)
            parts.Add("row rules " + string.Join(", ", row.Groups.SelectMany(g => g.Rules).Select(r => $"'{r.Id}'").Distinct()));
        var columns = section.Columns.Where(c => c.Dataset == dataset).Select(c => $"'{c.Column}' ({c.Treatment.ToString().ToLowerInvariant()})").Distinct().ToList();
        if (columns.Count > 0) parts.Add("column rules on " + string.Join(", ", columns));
        return string.Join("; ", parts);
    }
}
