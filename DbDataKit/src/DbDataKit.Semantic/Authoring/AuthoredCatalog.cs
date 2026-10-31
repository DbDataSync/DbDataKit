using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic.Authoring;

/// <summary>One problem found in an authored semantic file, with the place it was found. <see cref="Code"/> is the validation code (<c>unknown_column</c>, <c>binding_type_mismatch</c>, <c>semantic_yaml_invalid</c> ...).</summary>
public sealed record AuthoredProblem(string Code, string File, int Line, int Column, string Message, IReadOnlyList<string>? Suggestions = null)
{
    /// <summary><c>file:line:column: message</c>.</summary>
    public override string ToString() => $"{File}:{Line}:{Column}: {Message}";
}

/// <summary>A column of a physical table the host knows: its name, its catalog data type (<c>String</c>, <c>Integer</c>, <c>Decimal</c>, <c>Float</c>, <c>Boolean</c>, <c>Date</c>, <c>Time</c>, <c>DateTime</c>, <c>DateTimeTz</c>, <c>Opaque</c>).</summary>
public sealed record PhysicalColumn(string Name, string DataType);

/// <summary>What a host knows about the table a model is on one source: its physical name (as a query writes it), its columns and its key (the model's grain).</summary>
public sealed record PhysicalTable(string Name, IReadOnlyList<PhysicalColumn> Columns, IReadOnlyList<string> PrimaryKey);

/// <summary>
/// What a semantic file is checked against and its physical layer is generated from: the host's models. A host is DbDataBuild, whose models are the tables it builds
/// (or maps) on its connections; a source is one of its connections.
/// </summary>
public interface IPhysicalLayer
{
    /// <summary>The kind of the source (<c>sqlserver</c> or <c>ssas</c>, what the catalog format knows), or null when there is no such source.</summary>
    string? SourceKind(string source);

    /// <summary>True when the host has a model of that name at all, on any source.</summary>
    bool KnowsModel(string model);

    /// <summary>The table of a model on a source, or null when the model is not on that source.</summary>
    PhysicalTable? Table(string model, string source);

    /// <summary>The joins the host's models declare themselves (a foreign key of a model, plan 19 J29), by model. The semantic file reads them as relationships where both models are used and it says nothing of its own about that pair.</summary>
    IReadOnlyList<DeclaredRelationship> DeclaredRelationships => [];
}

/// <summary>A join a model declares: the foreign key's name (the relationship's), the two models and the columns of each, and where it is written (the model's file and line).</summary>
public sealed record DeclaredRelationship(string Name, string FromModel, string ToModel, IReadOnlyList<string> FromColumns, IReadOnlyList<string> ToColumns, string File, int Line);

/// <summary>A use of a model in a semantic file: where a binding, a measure or a relationship names it. <see cref="Source"/> is null for a relationship, which holds on every source.</summary>
public sealed record ModelUse(string Model, string? Source, string Path, int Line, int Column);

/// <summary>A use of a source (a connection) in a semantic file.</summary>
public sealed record SourceUse(string Source, string Path, int Line, int Column);

/// <summary>A table of the generated physical layer: the model it stands for on one source.</summary>
public sealed record GeneratedTable(string Name, string Model, string Source, PhysicalTable Physical);

/// <summary>What a member of the model is bound to: an attribute (<c>customers.region</c>) or a measure (<c>orders.revenue</c>), on a source, to a column of a model.</summary>
public sealed record MemberBinding(string Member, string Kind, string Source, string Model, string Column, string? Aggregate = null);

/// <summary>A relationship of the generated physical layer: an authored relationship on one source, between the generated tables of its two models.</summary>
public sealed record GeneratedRelationship(string Name, string FromModel, string ToModel, string Source, IReadOnlyList<string> FromColumns, IReadOnlyList<string> ToColumns, string? Role, int AuthoredIndex)
{
    /// <summary>The model file and line of the foreign key a relationship was derived from (<see cref="AuthoredIndex"/> is then -1); null for one the semantic file wrote.</summary>
    public (string File, int Line)? DeclaredAt { get; init; }
}

/// <summary>A semantic model compiled against a physical layer: the validated catalog and the physical layer that was generated for it.</summary>
public sealed class AuthoredCompilation
{
    internal AuthoredCompilation(Catalog catalog, IReadOnlyList<GeneratedTable> tables, IReadOnlyList<GeneratedRelationship> relationships, IReadOnlyDictionary<string, string> sources, IReadOnlyList<MemberBinding> bindings, JsonObject json)
        => (Catalog, Tables, Relationships, Sources, Bindings, Json) = (catalog, tables, relationships, sources, bindings, json);

    /// <summary>What every attribute and measure is bound to, in the order the file lists them.</summary>
    public IReadOnlyList<MemberBinding> Bindings { get; }

    /// <summary>The catalog as the engine loads it, physical layer included.</summary>
    public Catalog Catalog { get; }

    /// <summary>The same catalog as the document the engine loaded it from: what a server that serves the model is given (<see cref="CatalogLoader.LoadJson"/> and <see cref="CatalogLoader.LoadText"/> read it).</summary>
    public JsonObject Json { get; }

    /// <summary>The generated tables, one per model and source the file uses.</summary>
    public IReadOnlyList<GeneratedTable> Tables { get; }

    /// <summary>The generated relationships.</summary>
    public IReadOnlyList<GeneratedRelationship> Relationships { get; }

    /// <summary>The sources the file reads, with the kind each has in the catalog.</summary>
    public IReadOnlyDictionary<string, string> Sources { get; }
}

/// <summary>
/// A semantic model written without its physical layer (<see cref="AuthoringSchema"/>), read from a file. <see cref="Read"/> parses it strictly and checks it against the schema, naming
/// the file, line and column of what is wrong; <see cref="Compile"/> then generates the physical layer from the host's models and runs the catalog's own validation, mapping every
/// error back to the line of the authored file that caused it. Nothing here knows a product: the host supplies the models through <see cref="IPhysicalLayer"/>.
/// </summary>
public sealed class AuthoredCatalog
{
    private readonly YamlMapping _root;
    private readonly JsonObject _json;

    private AuthoredCatalog(string file, YamlMapping root, JsonObject json, List<ModelUse> models, List<SourceUse> sources)
        => (File, _root, _json, ModelUses, SourceUses) = (file, root, json, models, sources);

    /// <summary>The file the model was read from, as the host names it in messages.</summary>
    public string File { get; }

    /// <summary>The catalog's name (<c>name:</c>).</summary>
    public string Name => _json["name"]!.GetValue<string>();

    /// <summary>Every place the file names a model: bindings, measures and relationships.</summary>
    public IReadOnlyList<ModelUse> ModelUses { get; }

    /// <summary>Every place the file names a source.</summary>
    public IReadOnlyList<SourceUse> SourceUses { get; }

    /// <summary>The authored JSON as read (typed scalars), before a physical layer was added.</summary>
    public JsonObject Json => _json;

    /// <summary>
    /// Reads a semantic file. A syntax error, a duplicate key, an anchor or alias, a block that is generated (<c>tables</c>, a source's <c>kind</c>), or anything the schema refuses comes back as
    /// problems; the result is null when there is nothing to go on.
    /// </summary>
    public static AuthoredCatalog? Read(string yaml, string file, List<AuthoredProblem> problems)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, file, diagnostics);
        foreach (var d in diagnostics.Where(d => d.Severity == ConfigSeverity.Error))
            problems.Add(new AuthoredProblem("semantic_yaml_invalid", file, Math.Max(1, d.Line), Math.Max(1, d.Column), d.Message));
        if (diagnostics.Any(d => d.Severity == ConfigSeverity.Error)) return null;
        if (root is not YamlMapping mapping)
        {
            problems.Add(new AuthoredProblem("semantic_yaml_invalid", file, root?.Line ?? 1, root?.Column ?? 1,
                root is null ? "the file is empty; a semantic model is a mapping of blocks" : "a semantic model is a mapping of blocks, not a list or a value"));
            return null;
        }

        var before = problems.Count;
        if (mapping.GetEntry("tables") is { } tables)
            problems.Add(new AuthoredProblem("physical_layer_is_generated", file, tables.Key.Line, tables.Key.Column,
                "`tables` is not written in a project: the tables, their columns, types and keys are generated from the models. Name a model in each binding and measure instead."));
        if (mapping.Get("sources") is YamlMapping sources)
            foreach (var source in sources.Entries.Where(e => e.Value is YamlMapping))
                foreach (var key in ((YamlMapping)source.Value).Entries.Where(e => e.Key.Value is "kind" or "dialects"))
                    problems.Add(new AuthoredProblem("physical_layer_is_generated", file, key.Key.Line, key.Key.Column,
                        $"`{key.Key.Value}` of the source `{source.Key.Value}` is not written: a source is a connection of the project, and its kind comes from the connection's engine."));
        if (problems.Count > before) return null;

        var json = CatalogLoader.ToJson(mapping);
        var errors = SchemaStore.Errors(json, AuthoringSchema.Schema, "semantic_schema_invalid", "semantic");
        if (errors.Count > 0)
        {
            foreach (var e in errors) problems.Add(At(file, mapping, e));
            return null;
        }
        var (models, sourceUses) = Uses(mapping);
        return new AuthoredCatalog(file, mapping, json, models, sourceUses);
    }

    private static AuthoredProblem At(string file, YamlNode root, QisError error)
    {
        var at = PathLocator.Find(root, error.Path);
        // an error about a column points at the column, not at the binding that holds it
        if (error.Code is "unknown_column" or "column_expression_missing" or "column_expression_unsupported" && at.Node is YamlMapping m && m.Get("column") is { } column)
            return new AuthoredProblem(error.Code, file, column.Line, column.Column, error.Message, error.Suggestions);
        return new AuthoredProblem(error.Code, file, at.Line, at.Column, error.Message, error.Suggestions);
    }

    private static (List<ModelUse>, List<SourceUse>) Uses(YamlMapping root)
    {
        var models = new List<ModelUse>();
        var sources = new List<SourceUse>();
        if (root.Get("sources") is YamlMapping declared)
            foreach (var e in declared.Entries) sources.Add(new SourceUse(e.Key.Value, "$.sources." + e.Key.Value, e.Key.Line, e.Key.Column));
        if (root.Get("dimensions") is YamlMapping dimensions)
            foreach (var dimension in dimensions.Entries)
                if (dimension.Value is YamlMapping d && d.Get("attributes") is YamlMapping attributes)
                    foreach (var attribute in attributes.Entries)
                    {
                        if (attribute.Value is not YamlMapping a) continue;
                        var path = $"$.dimensions.{dimension.Key.Value}.{attribute.Key.Value}";
                        if (a.Get("members") is YamlMapping { } members && members.Get("from") is YamlScalar from)
                            sources.Add(new SourceUse(from.Value, path + ".members.from", from.Line, from.Column));
                        if (a.Get("bindings") is not YamlMapping bindings) continue;
                        foreach (var binding in bindings.Entries)
                        {
                            sources.Add(new SourceUse(binding.Key.Value, $"{path}.bindings.{binding.Key.Value}", binding.Key.Line, binding.Key.Column));
                            if (binding.Value is YamlMapping b && b.Get("table") is YamlScalar table)
                                models.Add(new ModelUse(table.Value, binding.Key.Value, $"{path}.bindings.{binding.Key.Value}.table", table.Line, table.Column));
                        }
                    }
        if (root.Get("measures") is YamlMapping measures)
            foreach (var measure in measures.Entries)
            {
                if (measure.Value is not YamlMapping m) continue;
                var source = m.Get("source") as YamlScalar;
                if (source is not null) sources.Add(new SourceUse(source.Value, $"$.measures.{measure.Key.Value}.source", source.Line, source.Column));
                if (m.Get("of") is YamlMapping of && of.Get("table") is YamlScalar table)
                    models.Add(new ModelUse(table.Value, source?.Value, $"$.measures.{measure.Key.Value}.of.table", table.Line, table.Column));
            }
        if (root.Get("relationships") is YamlSequence relationships)
            for (var i = 0; i < relationships.Items.Count; i++)
                if (relationships.Items[i] is YamlMapping r)
                    foreach (var end in new[] { "from", "to" })
                        if (r.Get(end) is YamlScalar model) models.Add(new ModelUse(model.Value, null, $"$.relationships[{i}].{end}", model.Line, model.Column));
        return (models, sources);
    }

    /// <summary>
    /// Generates the physical layer from <paramref name="layer"/> and validates the whole as a catalog. A source the layer does not know, a model it does not know, and a model that is not on the
    /// source a binding names are problems at the place they were written; so is everything the catalog validation finds (a column the model lacks, a type that disagrees, a relationship
    /// that is not many-to-one by the model's grain, an unreachable attribute ...), each at the line of the authored file it concerns. Null when there are problems.
    /// </summary>
    public AuthoredCompilation? Compile(IPhysicalLayer layer, List<AuthoredProblem> problems)
    {
        var before = problems.Count;
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var use in SourceUses)
        {
            if (kinds.ContainsKey(use.Source)) continue;
            if (layer.SourceKind(use.Source) is { } kind) kinds[use.Source] = kind;
            else problems.Add(new AuthoredProblem("unknown_source", File, use.Line, use.Column, $"`{use.Source}` is not a source: a source is a connection of the project that builds tables."));
        }
        // a name every use of which is by a model the layer lacks is reported once per use, so each line is found
        foreach (var use in ModelUses.Where(u => !layer.KnowsModel(u.Model)))
            problems.Add(new AuthoredProblem("unknown_model", File, use.Line, use.Column, $"`{use.Model}` is not a model of the project."));
        foreach (var use in ModelUses.Where(u => u.Source is not null && kinds.ContainsKey(u.Source) && layer.KnowsModel(u.Model) && layer.Table(u.Model, u.Source) is null))
            problems.Add(new AuthoredProblem("model_not_on_source", File, use.Line, use.Column, $"`{use.Model}` is not built or mapped on `{use.Source}`."));
        if (problems.Count > before) return null;

        // one table per model and source that something uses; the name is the model's when it is on one source, and `model@source` when it is on several
        var wanted = new List<(string Model, string Source)>();
        foreach (var use in ModelUses)
            foreach (var source in use.Source is { } s ? new[] { s } : kinds.Keys.Order(StringComparer.Ordinal).ToArray())
                if (!wanted.Contains((use.Model, source)) && layer.Table(use.Model, source) is not null) wanted.Add((use.Model, source));
        var onSeveral = wanted.GroupBy(w => w.Model).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        string TableName(string model, string source) => onSeveral.Contains(model) ? $"{model}@{source}" : model;

        var timeColumns = TimeColumns();
        var tables = wanted.Select(w =>
        {
            var physical = layer.Table(w.Model, w.Source)!;
            return new GeneratedTable(TableName(w.Model, w.Source), w.Model, w.Source, physical);
        }).ToList();

        var json = (JsonObject)_json.DeepClone();
        var sourcesJson = new JsonObject();
        var authoredSources = _json["sources"]?.AsObject();
        foreach (var (id, kind) in kinds.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var entry = new JsonObject { ["kind"] = kind };
            if (authoredSources?[id] is JsonObject settings)
                foreach (var (k, v) in settings) entry[k] = v?.DeepClone();
            sourcesJson[id] = entry;
        }
        json["sources"] = sourcesJson;

        json["tables"] = new JsonArray(tables.Select(t => (JsonNode)new JsonObject
        {
            ["name"] = t.Name,
            ["source"] = t.Physical.Name,
            ["x_source"] = t.Source,
            ["primary_key"] = t.Physical.PrimaryKey.Count > 0 ? new JsonArray(t.Physical.PrimaryKey.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()) : null,
            ["columns"] = new JsonArray(t.Physical.Columns.Select(c =>
            {
                var column = new JsonObject
                {
                    ["name"] = c.Name,
                    ["datatype"] = c.DataType,
                    ["expression"] = new JsonObject { ["dialects"] = new JsonArray(new JsonObject { ["dialect"] = "ANSI_SQL", ["expression"] = c.Name }) },
                };
                if (timeColumns.Contains((t.Model, t.Source, c.Name))) column["is_time"] = true;
                return (JsonNode)column;
            }).ToArray()),
        }).ToArray());
        foreach (var table in json["tables"]!.AsArray()) if (table!["primary_key"] is null) table.AsObject().Remove("primary_key");

        // the model names in bindings and measures become the generated tables' names, and the authored-only is_time leaves
        foreach (var attribute in AttributeNodes(json))
            if (attribute["bindings"] is JsonObject bindings)
                foreach (var (source, node) in bindings)
                    if (node is JsonObject binding)
                    {
                        binding["table"] = TableName(binding["table"]!.GetValue<string>(), source);
                        binding.Remove("is_time");
                    }
        foreach (var (_, measure) in json["measures"]!.AsObject())
            if (measure is JsonObject m && m["of"] is JsonObject of && m["source"] is JsonNode source)
                of["table"] = TableName(of["table"]!.GetValue<string>(), source.GetValue<string>());

        // relationships are written between models and hold on every source where both are generated
        var relationships = new List<GeneratedRelationship>();
        var relationshipJson = new JsonArray();
        if (_json["relationships"] is JsonArray authored)
            for (var i = 0; i < authored.Count; i++)
            {
                var r = authored[i]!.AsObject();
                var (from, to) = (r["from"]!.GetValue<string>(), r["to"]!.GetValue<string>());
                foreach (var source in kinds.Keys.Order(StringComparer.Ordinal))
                {
                    if (!tables.Any(t => t.Model == from && t.Source == source) || !tables.Any(t => t.Model == to && t.Source == source)) continue;
                    var name = r["name"]!.GetValue<string>();
                    var generated = new JsonObject
                    {
                        ["name"] = kinds.Count > 1 ? $"{name}@{source}" : name,
                        ["from"] = TableName(from, source),
                        ["to"] = TableName(to, source),
                        ["from_columns"] = r["from_columns"]!.DeepClone(),
                        ["to_columns"] = r["to_columns"]!.DeepClone(),
                    };
                    if (r["x_role"] is { } role) generated["x_role"] = role.DeepClone();
                    relationshipJson.Add(generated);
                    relationships.Add(new GeneratedRelationship(generated["name"]!.GetValue<string>(), from, to, source, Strings(r["from_columns"]), Strings(r["to_columns"]), r["x_role"]?.GetValue<string>(), i));
                }
            }
        // a foreign key of a model is a relationship too (J29), where both its models are used by this file and the file says nothing of its own between them (roles and several keys
        // between two models stay in the file); a name the file already uses for another join is a problem at the key, not a silent second join
        var authoredNames = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var authoredPairs = new HashSet<(string, string)>();
        if (_json["relationships"] is JsonArray written)
            foreach (var w in written.OfType<JsonObject>()) { authoredNames[w["name"]!.GetValue<string>()] = w; authoredPairs.Add((w["from"]!.GetValue<string>(), w["to"]!.GetValue<string>())); }
        var usedModels = tables.Select(t => t.Model).ToHashSet(StringComparer.Ordinal);
        foreach (var group in layer.DeclaredRelationships.Where(d => usedModels.Contains(d.FromModel) && usedModels.Contains(d.ToModel)).GroupBy(d => (d.FromModel, d.ToModel)))
        {
            if (authoredPairs.Contains(group.Key) || group.Count() != 1) continue;
            var d = group.Single();
            if (authoredNames.ContainsKey(d.Name))
            {
                problems.Add(new AuthoredProblem("relationship_name_taken", d.File, d.Line, 1, $"the foreign key `{d.Name}` of `{d.FromModel}` would be the relationship `{d.Name}`, a name `{File}` already uses for another join: rename one of them."));
                continue;
            }
            foreach (var source in kinds.Keys.Order(StringComparer.Ordinal))
            {
                if (!tables.Any(t => t.Model == d.FromModel && t.Source == source) || !tables.Any(t => t.Model == d.ToModel && t.Source == source)) continue;
                var generated = new JsonObject
                {
                    ["name"] = kinds.Count > 1 ? $"{d.Name}@{source}" : d.Name,
                    ["from"] = TableName(d.FromModel, source),
                    ["to"] = TableName(d.ToModel, source),
                    ["from_columns"] = new JsonArray(d.FromColumns.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
                    ["to_columns"] = new JsonArray(d.ToColumns.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
                };
                relationshipJson.Add(generated);
                relationships.Add(new GeneratedRelationship(generated["name"]!.GetValue<string>(), d.FromModel, d.ToModel, source, d.FromColumns, d.ToColumns, null, -1) { DeclaredAt = (d.File, d.Line) });
            }
        }
        json["relationships"] = relationshipJson;
        if (relationshipJson.Count == 0) json.Remove("relationships");

        try
        {
            var catalog = CatalogLoader.LoadJson(json);
            return new AuthoredCompilation(catalog, tables, relationships, kinds, BindingsOf(), json);
        }
        catch (CatalogException e)
        {
            var seen = new HashSet<(string, int, int, string)>();
            // a measure or binding that names a column the model lacks leaves its source without a fact table, and every attribute of that source then reads as unreachable: the
            // cause is the one to show
            var cause = e.Errors.Any(x => x.Code is "unknown_column" or "unknown_table" or "column_expression_missing" or "column_expression_unsupported");
            foreach (var error in e.Errors.Where(x => !(cause && x.Code == "unreachable_attribute")))
            {
                var located = Remap(error, relationships);
                if (seen.Add((located.Code, located.Line, located.Column, located.Message))) problems.Add(located);
            }
            return null;
        }
    }

    private List<MemberBinding> BindingsOf()
    {
        var found = new List<MemberBinding>();
        foreach (var (dimension, node) in _json["dimensions"]!.AsObject())
            foreach (var (attribute, a) in node!["attributes"]!.AsObject())
                foreach (var (source, b) in a!["bindings"]!.AsObject())
                    found.Add(new MemberBinding($"{dimension}.{attribute}", "attribute", source, b!["table"]!.GetValue<string>(), b["column"]!.GetValue<string>()));
        foreach (var (name, m) in _json["measures"]!.AsObject())
            if (m!["of"] is JsonObject of && m["source"] is { } source)
                found.Add(new MemberBinding(name, "measure", source.GetValue<string>(), of["table"]!.GetValue<string>(), of["column"]!.GetValue<string>(), m["agg"]?.GetValue<string>()));
        return found;
    }

    /// <summary>Where a validation path (<c>$.hierarchies.date.by_month</c>) is written in the file: line and column, 1-based.</summary>
    public (int Line, int Column) Locate(string path)
    {
        var at = PathLocator.Find(_root, path);
        return (at.Line, at.Column);
    }

    /// <summary>(model, source, column) of every binding that says <c>is_time</c>.</summary>
    private HashSet<(string Model, string Source, string Column)> TimeColumns()
    {
        var found = new HashSet<(string, string, string)>();
        foreach (var attribute in AttributeNodes(_json))
            if (attribute["bindings"] is JsonObject bindings)
                foreach (var (source, node) in bindings)
                    if (node is JsonObject { } b && b["is_time"]?.GetValue<bool>() == true)
                        found.Add((b["table"]!.GetValue<string>(), source, b["column"]!.GetValue<string>()));
        return found;
    }

    private static IEnumerable<JsonObject> AttributeNodes(JsonObject json) =>
        json["dimensions"]!.AsObject().SelectMany(d => d.Value!["attributes"]!.AsObject().Select(a => a.Value!.AsObject()));

    private static List<string> Strings(JsonNode? node) => node?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];

    /// <summary>An error of the generated catalog, put back where the person wrote the thing it is about.</summary>
    private AuthoredProblem Remap(QisError error, List<GeneratedRelationship> relationships)
    {
        var path = error.Path;
        // $.relationships[i] of the generated list is the authored one it came from
        if (System.Text.RegularExpressions.Regex.Match(path, @"^\$\.relationships\[(\d+)\]") is { Success: true } derived && int.Parse(derived.Groups[1].Value) < relationships.Count && relationships[int.Parse(derived.Groups[1].Value)].DeclaredAt is { } at)
            return new AuthoredProblem(error.Code, at.File, at.Line, 1, $"the foreign key `{relationships[int.Parse(derived.Groups[1].Value)].Name.Split('@')[0]}`, read as a relationship: {error.Message}", error.Suggestions);
        if (System.Text.RegularExpressions.Regex.Match(path, @"^\$\.relationships\[(\d+)\]") is { Success: true } m && int.Parse(m.Groups[1].Value) < relationships.Count)
            path = $"$.relationships[{relationships[int.Parse(m.Groups[1].Value)].AuthoredIndex}]" + path[m.Length..];
        else if (path.StartsWith("$.tables", StringComparison.Ordinal)) path = "$";
        return At(File, _root, error with { Path = path });
    }
}
