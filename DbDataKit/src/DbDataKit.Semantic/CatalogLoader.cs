using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Semantic.Abstractions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Semantic;

/// <summary>The catalog failed validation. <see cref="Errors"/> carry the catalog validation codes.</summary>
public sealed class CatalogException(List<QisError> errors)
    : Exception("Invalid catalog: " + string.Join("; ", errors.Select(e => $"{e.Code} at {e.Path}: {e.Message}")))
{
    public List<QisError> Errors { get; } = errors;
}

/// <summary>
/// Reads the catalog format (docs/catalog.md): an Apache Ossie-shaped physical model plus QIS blocks. It validates the
/// document against catalog.schema.json, checks it as a model (keys, reachability, rollup rules), and builds the
/// resolved model the emitters use: a fact table per source, joins from relationship columns, generated aliases.
/// </summary>
public static partial class CatalogLoader
{
    /// <summary>The breakdowns of a datetime dimension, finest first.</summary>
    public static readonly string[] DatetimeGranularities = ["hour", "day", "week", "month", "quarter", "year"];

    /// <summary>The node as parsed from its own text, so a number read from YAML is the int or long a request carries and not a decimal.</summary>
    private static JsonNode? AsWritten(JsonNode? node) => node is null ? null : JsonNode.Parse(node.ToJsonString());

    private static QisError Err(string code, string path, string message, List<string>? suggestions = null) =>
        new("catalog", code, path, message, suggestions is { Count: > 0 } ? suggestions : null);

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();

    /// <summary>Reads a catalog file. Any error names the file and the line of what it found.</summary>
    public static Catalog Load(string path) => LoadText(File.ReadAllText(path), path);

    /// <summary>
    /// Reads catalog text. With <paramref name="file"/> (the name the errors report), a malformed document or a failed check comes back as a
    /// <see cref="CatalogException"/> whose messages start with <c>file:line:column</c>.
    /// </summary>
    public static Catalog LoadText(string yaml, string? file = null)
    {
        var root = ReadYaml(yaml, file ?? "catalog");
        try { return LoadJson(ToJson(root)); }
        catch (CatalogException e) when (file is not null)
        {
            throw new CatalogException(e.Errors.Select(x => Locate(root, file, x)).ToList());
        }
    }

    /// <summary>Loads a catalog already read as JSON, for example one imported from Ossie documents.</summary>
    public static Catalog LoadJson(JsonObject json)
    {
        var errors = SchemaStore.Errors(json, SchemaStore.Get("catalog"), "catalog_schema_invalid", "catalog");
        if (errors.Count > 0) throw new CatalogException(errors);
        errors = [];
        var doc = Build(json, errors);
        if (errors.Count > 0) throw new CatalogException(errors);
        return new Catalog(doc, json);
    }

    // ---------- YAML to typed JSON ----------

    /// <summary>
    /// YAML to JSON, with plain scalars typed (true, 3, 0.5, null) so the document can be checked against a JSON Schema.
    /// Quoted scalars stay strings. The reader is the suite's strict one (<c>DbDataKit.Config.Yaml</c>): a duplicate key, an anchor, an alias,
    /// a tag or a merge key is an error, not silently read.
    /// </summary>
    public static JsonObject YamlToJson(string yaml) => ToJson(ReadYaml(yaml, "catalog"));

    internal static YamlMapping ReadYaml(string yaml, string file)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, file, diagnostics);
        var errors = diagnostics.Where(d => d.Severity == ConfigSeverity.Error)
            .Select(d => Err("catalog_yaml_invalid", "$", d.ToString())).ToList();
        if (errors.Count == 0 && root is not YamlMapping)
            errors.Add(Err("catalog_yaml_invalid", "$", root is null ? $"{file}: the file is empty; a catalog is a mapping" : $"{file}:{root.Line}:{root.Column}: a catalog is a mapping of blocks, not a {(root is YamlSequence ? "list" : "value")}"));
        if (errors.Count > 0) throw new CatalogException(errors);
        return (YamlMapping)root!;
    }

    internal static JsonObject ToJson(YamlMapping root) => (JsonObject)Convert(root)!;

    private static JsonNode? Convert(YamlNode node) => node switch
    {
        YamlMapping m => new JsonObject(m.Entries.Select(e => KeyValuePair.Create<string, JsonNode?>(e.Key.Value, Convert(e.Value)))),
        YamlSequence s => new JsonArray(s.Items.Select(Convert).ToArray()),
        YamlScalar { Quoted: false } sc => Plain(sc.Value),
        YamlScalar sc => JsonValue.Create(sc.Value),
        _ => null,
    };

    /// <summary>An error with the file, line and column of the node its path names (the deepest one that exists), as <c>file:line:column</c> in front of the message.</summary>
    private static QisError Locate(YamlNode root, string file, QisError error)
    {
        var at = Authoring.PathLocator.Find(root, error.Path);
        return error with { Message = $"{file}:{at.Line}:{at.Column}: {error.Message}" };
    }

    private static JsonNode? Plain(string? v)
    {
        if (v is null or "null" or "~" or "") return null;
        if (v is "true" or "false") return JsonValue.Create(v == "true");
        // Every number is a decimal, so a consumer reads 0 and 0.5 the same way.
        if (long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create((decimal)l);
        if (v.Contains('.') && decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(v);
    }

    // ---------- model checks and the resolved model ----------

    private static object? Plain(JsonNode? n) => n switch
    {
        JsonObject o => o.ToDictionary(kv => (object)kv.Key, kv => Plain(kv.Value)!),
        JsonArray a => a.Select(Plain).ToList(),
        JsonValue v => v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : v.ToString(),
        _ => null,
    };

    /// <summary>
    /// The catalog's dimensions as one flat map of attributes keyed dimension.attribute, which is what the rest of the loader works on. Each attribute carries
    /// its dimension's default role (as <c>x_default_role</c>), and the dimensions themselves are recorded on the document.
    /// </summary>
    private static JsonObject FlattenAttributes(JsonObject root, CatalogDoc doc)
    {
        var flat = new JsonObject();
        foreach (var (dimensionName, node) in root["dimensions"]!.AsObject())
        {
            var dimension = node!.AsObject();
            doc.Dimensions[dimensionName] = new DimensionDef { DefaultRole = dimension["default_role"]?.GetValue<string>(), Label = dimension["label"]?.GetValue<string>() };
            foreach (var (attributeName, attribute) in dimension["attributes"]!.AsObject())
            {
                var copy = attribute!.DeepClone().AsObject();
                if (dimension["default_role"] is { } role) copy["x_default_role"] = role.DeepClone();
                flat[$"{dimensionName}.{attributeName}"] = copy;
            }
        }
        return flat;
    }

    private static string? AnsiExpression(JsonNode colName) =>
        colName["expression"]!["dialects"]!.AsArray()
            .FirstOrDefault(d => d!["dialect"]!.GetValue<string>() == "ANSI_SQL")?["expression"]?.GetValue<string>();

    /// <summary>The model table in a column's DAX expression (<c>'Budget'[Amount]</c> gives <c>'Budget'</c>), or null when the column has none.</summary>
    private static string? DaxTable(JsonNode colName) =>
        colName["expression"]!["dialects"]!.AsArray()
            .FirstOrDefault(d => d!["dialect"]!.GetValue<string>() == "DAX")?["expression"]?.GetValue<string>() is { } dax
        && System.Text.RegularExpressions.Regex.Match(dax, @"^\s*('[^']+'|[A-Za-z_][\w]*)\s*\[") is { Success: true } m
            ? (m.Groups[1].Value.StartsWith('\'') ? m.Groups[1].Value : $"'{m.Groups[1].Value}'") : null;

    private static string LogicalType(string datatype) => datatype switch
    {
        "Integer" => "integer", "Decimal" or "Float" => "number", "Boolean" => "boolean",
        "Date" => "date", "DateTime" or "DateTimeTz" => "datetime", _ => "string",
    };

    private static CatalogDoc Build(JsonObject root, List<QisError> errors)
    {
        var doc = new CatalogDoc();
        if (root["timezone"]?.GetValue<string>() is { } zone)
        {
            doc.Timezone = zone;
            if (!TimeZones.IsValid(zone)) errors.Add(Err("unknown_timezone", "$.timezone", $"{zone} isn't a time zone; use an IANA name such as America/Chicago or UTC."));
        }
        var sources = root["sources"]!.AsObject();
        foreach (var (sourceId, sourceNode) in sources)
            if (sourceNode!["datetime_zone"]?.GetValue<string>() is { } storedIn && !TimeZones.IsValid(storedIn))
                errors.Add(Err("unknown_timezone", $"$.sources.{sourceId}.datetime_zone", $"{storedIn} isn't a time zone; use an IANA name such as America/Chicago or UTC."));
        var attrs = FlattenAttributes(root, doc);
        var tables = root["tables"]!.AsArray().Select(d => d!.AsObject()).ToList();
        var byName = new Dictionary<string, JsonObject>();
        for (int i = 0; i < tables.Count; i++)
        {
            var name = tables[i]["name"]!.GetValue<string>();
            if (!byName.TryAdd(name, tables[i]))
                errors.Add(Err("duplicate_member", $"$.tables[{i}].name", $"Table {name} is declared twice."));
            var src = tables[i]["x_source"]!.GetValue<string>();
            if (!sources.ContainsKey(src))
                errors.Add(Err("unknown_source", $"$.tables[{i}].x_source", $"{src} isn't in sources."));
        }

        string? DaxTableOf(string table, string colName) => ColumnDef(table, colName) is { } f ? DaxTable(f) : null;

        JsonObject? ColumnDef(string table, string colName) =>
            byName.TryGetValue(table, out var ds)
                ? ds["columns"]?.AsArray().Select(f => f!.AsObject()).FirstOrDefault(f => f["name"]!.GetValue<string>() == colName)
                : null;

        // A table, column and, if used as a column, a plain-identifier expression.
        string? Column(string table, string colName, string path)
        {
            if (!byName.ContainsKey(table)) { errors.Add(Err("unknown_table", path, $"{table} isn't a table.")); return null; }
            var f = ColumnDef(table, colName);
            if (f is null) { errors.Add(Err("unknown_column", path, $"{table} has no column {colName}.")); return null; }
            var expr = AnsiExpression(f);
            if (expr is null) { errors.Add(Err("column_expression_missing", path, $"{table}.{colName} has no ANSI_SQL expression.")); return null; }
            if (!Identifier().IsMatch(expr))
            { errors.Add(Err("column_expression_unsupported", path, $"{table}.{colName} is the expression {expr}; only a plain column can be used so far.")); return null; }
            return expr;
        }

        for (int i = 0; i < tables.Count; i++)
        {
            var fields = tables[i]["columns"]?.AsArray().Select(f => f!["name"]!.GetValue<string>()).ToList() ?? [];
            foreach (var key in (tables[i]["primary_key"]?.AsArray().Select(k => k!.GetValue<string>()) ?? [])
                         .Concat(tables[i]["unique_keys"]?.AsArray().SelectMany(k => k!.AsArray().Select(x => x!.GetValue<string>())) ?? []))
                if (!fields.Contains(key))
                    errors.Add(Err("unknown_column", $"$.tables[{i}]", $"Key column {key} isn't a column of {tables[i]["name"]}."));
        }

        // Relationships are many-to-one: the columns of `to` must be one of its keys.
        var relationships = root["relationships"]?.AsArray().Select(r => r!.AsObject()).ToList() ?? [];
        for (int i = 0; i < relationships.Count; i++)
        {
            var r = relationships[i];
            var (from, to) = (r["from"]!.GetValue<string>(), r["to"]!.GetValue<string>());
            var (fromCols, toCols) = (Strings(r["from_columns"]), Strings(r["to_columns"]));
            var p = $"$.relationships[{i}]";
            if (!byName.ContainsKey(from)) { errors.Add(Err("unknown_table", p + ".from", $"{from} isn't a table.")); continue; }
            if (!byName.ContainsKey(to)) { errors.Add(Err("unknown_table", p + ".to", $"{to} isn't a table.")); continue; }
            if (fromCols.Count != toCols.Count)
            { errors.Add(Err("relationship_not_many_to_one", p, "from_columns and to_columns differ in length.")); continue; }
            foreach (var c in fromCols) if (ColumnDef(from, c) is null) errors.Add(Err("unknown_column", p + ".from_columns", $"{from} has no colName {c}."));
            foreach (var c in toCols) if (ColumnDef(to, c) is null) errors.Add(Err("unknown_column", p + ".to_columns", $"{to} has no colName {c}."));
            var keys = new List<List<string>>();
            if (byName[to]["primary_key"] is JsonArray pk) keys.Add(Strings(pk));
            if (byName[to]["unique_keys"] is JsonArray uks) keys.AddRange(uks.Select(k => Strings(k)));
            if (!keys.Any(k => k.Count == toCols.Count && k.All(toCols.Contains)))
                errors.Add(Err("relationship_not_many_to_one", p,
                    $"{string.Join(", ", toCols)} isn't a primary or unique key of {to}, so {from} to {to} isn't many-to-one."));
        }

        // Measures: each names the fact table it aggregates, which belongs to its source. A source may have several
        // facts; the first keeps the source's name and each further one is named source.table.
        var factsOf = new Dictionary<string, List<string>>();
        foreach (var (name, m) in root["measures"]!.AsObject())
        {
            var p = $"$.measures.{name}";
            if (m!["compute"] is not null) continue;   // derived measures have no source; they are checked below
            var source = m!["source"]!.GetValue<string>();
            var of = m["of"]!;
            var (tableName, colName) = (of["table"]!.GetValue<string>(), of["column"]!.GetValue<string>());
            if (!sources.ContainsKey(source)) { errors.Add(Err("unknown_source", p + ".source", $"{source} isn't in sources.")); continue; }
            var column = Column(tableName, colName, p + ".of");
            if (column is null) continue;
            if (byName[tableName]["x_source"]!.GetValue<string>() != source)
                errors.Add(Err("measure_table_source_mismatch", p + ".of", $"{tableName} belongs to {byName[tableName]["x_source"]}, not to {source}."));
            if (!factsOf.TryGetValue(source, out var list)) factsOf[source] = list = [];
            if (!list.Contains(tableName)) list.Add(tableName);
            if (attrs.ContainsKey(name))
                errors.Add(Err("duplicate_member", p, $"{name} is both a dimension and a measure."));
        }
        string SourceId(string source, string fact) => factsOf[source][0] == fact ? source : $"{source}.{fact}";

        // The joins of each fact: every table reachable by many-to-one relationships, however many hops. A table
        // reached by relationships with different roles (order date, ship date) is joined once per role.
        var joinsOf = new Dictionary<string, Dictionary<string, JoinDef>>();
        foreach (var (source, def) in sources.Select(s => (s.Key, s.Value!.AsObject())))
        {
            if (!factsOf.TryGetValue(source, out var facts)) { doc.Sources[source] = new SourceDef { Kind = def["kind"]!.GetValue<string>(), Totals = def["totals"]?.GetValue<string>(), DatetimeZone = def["datetime_zone"]?.GetValue<string>() ?? "UTC" }; continue; }
            foreach (var fact in facts)
            {
                var sd = new SourceDef
                {
                    Kind = def["kind"]!.GetValue<string>(),
                    Totals = def["totals"]?.GetValue<string>(),
                    DatetimeZone = def["datetime_zone"]?.GetValue<string>() ?? "UTC",
                    Fact = new JoinDef { Table = byName[fact]["source"]!.GetValue<string>(), Alias = "f" },
                };
                var used = new HashSet<string> { "f" };
                var queue = new Queue<(string Table, string? Key)>();
                queue.Enqueue((fact, null));
                var visited = new HashSet<string> { fact };
                while (queue.Count > 0)
                {
                    var (from, parentKey) = queue.Dequeue();
                    foreach (var r in relationships.Where(r => r["from"]!.GetValue<string>() == from && byName.ContainsKey(r["to"]!.GetValue<string>())))
                    {
                        var to = r["to"]!.GetValue<string>();
                        var role = r["x_role"]?.GetValue<string>();
                        var key = role is null ? to : $"{to}#{role}";
                        if (sd.Joins.ContainsKey(key))
                        {
                            errors.Add(Err("ambiguous_relationship", $"$.relationships[{relationships.IndexOf(r)}]",
                                $"{fact} reaches {to} by more than one relationship; give each an x_role (order_date, ship_date) so a dimension can say which it means."));
                            continue;
                        }
                        var alias = NextAlias(to, used);
                        var parentAlias = parentKey is null ? "f" : sd.Joins[parentKey].Alias;
                        var cond = Strings(r["from_columns"]).Zip(Strings(r["to_columns"]), (fc, tc) =>
                            (From: ColumnDef(from, fc) is { } ff ? AnsiExpression(ff) : null, To: ColumnDef(to, tc) is { } tf ? AnsiExpression(tf) : null))
                            .Where(c => c.From is not null && c.To is not null && Identifier().IsMatch(c.From) && Identifier().IsMatch(c.To))
                            .Select(c => $"{alias}.{c.To} = {parentAlias}.{c.From}").ToList();
                        sd.Joins[key] = new JoinDef { Table = byName[to]["source"]!.GetValue<string>(), Alias = alias, Condition = string.Join(" AND ", cond), Parent = parentKey };
                        if (visited.Add(to) || role is not null) queue.Enqueue((to, key));
                    }
                }
                doc.Sources[SourceId(source, fact)] = sd;
            }
        }

        var instances = new Dictionary<string, AttributeDef>();
        foreach (var (name, d) in attrs)
        {
            var attribute = new AttributeDef
            {
                Type = d!["type"]!.GetValue<string>(), SortKey = d["sort_key"]?.GetValue<string>(),
                Synonyms = Strings(d["synonyms"]),
            };
            attribute.DateKey = d["date"]?.GetValue<string>();
            attribute.IsTime = IsTime(attrs, byName, name);
            if (d["members"] is JsonArray listed) attribute.MemberList = Strings(listed);
            else if (d["members"]?["from"] is { } from)
            {
                attribute.MembersFrom = from.GetValue<string>();
                if (!sources.ContainsKey(attribute.MembersFrom))
                    errors.Add(Err("members_unknown_source", $"$.dimensions.{name}.members.from", $"{attribute.MembersFrom} isn't in sources."));
                else if (!d["bindings"]!.AsObject().ContainsKey(attribute.MembersFrom))
                    errors.Add(Err("members_source_unbound", $"$.dimensions.{name}.members.from", $"{name} has no binding in {attribute.MembersFrom}, so that source can't list its members."));
            }
            foreach (var (source, b) in d["bindings"]!.AsObject())
            {
                var p = $"$.dimensions.{name}.bindings.{source}";
                var (tableName, colName) = (b!["table"]!.GetValue<string>(), b["column"]!.GetValue<string>());
                var defaultRole = d["x_default_role"]?.GetValue<string>();
                if (!sources.ContainsKey(source)) { errors.Add(Err("unknown_source", p, $"{source} isn't in sources.")); continue; }
                var column = Column(tableName, colName, p);
                if (column is null) continue;
                var f = ColumnDef(tableName, colName)!;
                if (attribute.Type == "datetime" && sources[source]!["kind"]!.GetValue<string>() == "ssas")
                { errors.Add(Err("datetime_unsupported_source", p, $"{name} is a datetime and {source} is an SSAS model: DAX has no time zone conversion, so a datetime can't be bound there. Bind a date or a text column instead.")); continue; }
                if (f["datatype"] is { } dt && LogicalType(dt.GetValue<string>()) != attribute.Type)
                    errors.Add(Err("binding_type_mismatch", p, $"{name} is {attribute.Type} but {tableName}.{colName} is {dt}."));

                // Bound for every fact table of the source that can reach the table; not bound (so spread) for the rest.
                var reachedBy = 0;
                foreach (var fact in factsOf.GetValueOrDefault(source, []))
                {
                    var sourceDef = doc.Sources[SourceId(source, fact)];
                    string? join;
                    string? role = null;   // the role this attribute's bare name reads, when its table is reached in several
                    if (fact == tableName) join = null;
                    else
                    {
                        var keys = sourceDef.Joins.Keys.Where(k => k == tableName || k.StartsWith(tableName + "#")).ToList();
                        if (keys.Any(k => k.StartsWith(tableName + "#")))
                        {
                            if (defaultRole is null)
                            {
                                errors.Add(Err("ambiguous_relationship", p, $"{tableName} is reached by roles {string.Join(", ", keys.Select(k => k[(tableName.Length + 1)..]))}; give the dimension a default_role."));
                                continue;
                            }
                            if (!keys.Contains($"{tableName}#{defaultRole}"))
                            {
                                errors.Add(Err("unknown_role", p, $"{defaultRole} isn't a role of {tableName}; its roles are {string.Join(", ", keys.Select(k => k[(tableName.Length + 1)..]))}.",
                                    keys.Select(k => k[(tableName.Length + 1)..]).ToList()));
                                continue;
                            }
                            role = defaultRole;
                            keys = keys.Where(k => k == $"{tableName}#{role}").ToList();
                        }
                        if (keys.Count == 0) continue;
                        join = keys[0];
                    }
                    reachedBy++;
                    attribute.Bindings[SourceId(source, fact)] = new BindingDef { Join = join, Column = column, Dax = b["native"]?["DAX"]?.GetValue<string>() };
                    // A role-playing table: its bare dimension reads the binding's role, and each role (the default too) is also a dimension of its own.
                    if (role is not null)
                    {
                        attribute.DefaultRole = role;
                        foreach (var key in sourceDef.Joins.Keys.Where(k => k.StartsWith(tableName + "#")))
                        {
                            var r = key[(tableName.Length + 1)..];
                            var instanceName = $"{r}.{name[(name.IndexOf('.') + 1)..]}";
                            if (!instances.TryGetValue(instanceName, out var inst))
                                instances[instanceName] = inst = new AttributeDef
                                {
                                    Type = attribute.Type, SortKey = attribute.SortKey, Instance = (name, r), IsTime = attribute.IsTime,
                                    Synonyms = attribute.Synonyms.Select(x => $"{r.Replace('_', ' ')} {x}").ToList(),
                                };
                            inst.Bindings[SourceId(source, fact)] = new BindingDef { Join = key, Column = column, Dax = b["native"]?["DAX"]?.GetValue<string>() };
                        }
                    }
                }
                if (reachedBy == 0)
                    errors.Add(Err("unreachable_attribute", p, $"{tableName} has no many-to-one relationship path from any fact table of {source}."));
            }
            doc.Attributes[name] = attribute;
        }
        foreach (var (instanceName, inst) in instances)
        {
            var role = inst.Instance!.Value.Role;
            if (doc.Attributes.Keys.Any(k => doc.Attributes[k].Instance is null && k.StartsWith(role + ".")))
                errors.Add(Err("role_name_collision", $"$.dimensions.{instanceName}", $"The role {role} has the same name as a dimension ({role}.…), so {role}.{instanceName[(instanceName.IndexOf('.') + 1)..]} would be ambiguous. Give the role a different name."));
            else if (doc.Attributes.ContainsKey(instanceName))
                errors.Add(Err("role_name_collision", $"$.dimensions.{instanceName}", $"{instanceName} is already a dimension, so it can't also be {inst.Instance!.Value.Base} read in the role {inst.Instance.Value.Role}."));
            else doc.Attributes[instanceName] = inst;
        }

        // A datetime dimension can be broken down by hour, day, week, month, quarter or year in the request's time zone: each is a dimension of its own,
        // named attribute_granularity, whose values are labels ("2026-07", "2026-Q3").
        foreach (var (name, attribute) in doc.Attributes.Where(kv => kv.Value.Type == "datetime" && kv.Value.Instance is null && kv.Value.Bucket is null).ToList())
            foreach (var granularity in DatetimeGranularities)
            {
                var bucketName = $"{name}_{granularity}";
                if (doc.Attributes.ContainsKey(bucketName))
                { errors.Add(Err("duplicate_member", $"$.dimensions.{bucketName}", $"{bucketName} is already a dimension, so it can't be {name} by {granularity}.")); continue; }
                doc.Attributes[bucketName] = new AttributeDef
                {
                    Type = "string", Bucket = (name, granularity), SortKey = null, IsTime = true,
                    Synonyms = attribute.Synonyms.Select(x => $"{x} by {granularity}").ToList(),
                    Bindings = attribute.Bindings.ToDictionary(kv => kv.Key, kv => new BindingDef { Join = kv.Value.Join, Column = kv.Value.Column }),
                };
            }

        // A datetime tied to a date dimension (date: date.calendar_date) reads that dimension's attributes through its local date: orders.ordered_at_fiscal_month is
        // the fiscal month of the day the datetime falls on in the request's zone. Each attribute of the date dimension becomes a dimension of the datetime.
        foreach (var (name, datetimeAttribute) in doc.Attributes.Where(kv => kv.Value.DateKey is not null).ToList())
        {
            var key = datetimeAttribute.DateKey!;
            if (datetimeAttribute.Type != "datetime") { errors.Add(Err("date_link_not_datetime", $"$.dimensions.{name}.date", $"{name} is a {datetimeAttribute.Type}; only a datetime can be tied to a date dimension.")); continue; }
            if (!doc.Attributes.TryGetValue(key, out var keyAttribute) || keyAttribute.Type != "date")
            { errors.Add(Err("date_link_unknown_attribute", $"$.dimensions.{name}.date", $"{key} isn't a date dimension.")); continue; }
            var owner = key.Split('.')[0];
            foreach (var (attribute, attr) in doc.Attributes.Where(kv => kv.Key.StartsWith(owner + ".") && kv.Value.Instance is null && kv.Value.Linked is null && kv.Value.Bucket is null).ToList())
            {
                var derivedName = $"{name}_{attribute[(owner.Length + 1)..]}";
                if (doc.Attributes.ContainsKey(derivedName))
                { errors.Add(Err("duplicate_member", $"$.dimensions.{derivedName}", $"{derivedName} is already a dimension, so it can't be {attribute} read through {name}.")); continue; }
                var derived = new AttributeDef
                {
                    Type = attr.Type, SortKey = null, Linked = (name, attribute), IsTime = attr.IsTime,
                    Synonyms = attr.Synonyms.Select(x => $"{x} of the {name[(name.IndexOf('.') + 1)..].Replace('_', ' ')}").ToList(),
                };
                foreach (var (sourceId, datetimeBinding) in datetimeAttribute.Bindings)
                {
                    if (!keyAttribute.Bindings.TryGetValue(sourceId, out var keyBinding) || !attr.Bindings.TryGetValue(sourceId, out var attrBinding)) continue;
                    var sd = doc.Sources[sourceId];
                    var linkKey = $"{keyBinding.Join}@{name}";
                    if (!sd.Joins.ContainsKey(linkKey))
                    {
                        var table = sd.Joins[keyBinding.Join!];
                        var aliases = sd.Joins.Values.Select(j => j.Alias).Append(sd.Fact.Alias).ToHashSet();
                        var datetimeAlias = datetimeBinding.Join is not null ? sd.Joins[datetimeBinding.Join].Alias : sd.Fact.Alias;
                        sd.Joins[linkKey] = new JoinDef
                        {
                            Table = table.Table, Alias = NextAlias(table.Table, aliases),
                            LocalDateOf = $"{datetimeAlias}.{datetimeBinding.Column}", DateColumn = keyBinding.Column, Parent = datetimeBinding.Join,
                        };
                    }
                    if (attrBinding.Join != keyBinding.Join) continue;   // another table's attribute (a snowflake) isn't read through the date
                    derived.Bindings[sourceId] = new BindingDef { Join = linkKey, Column = attrBinding.Column };
                }
                if (derived.Bindings.Count > 0) doc.Attributes[derivedName] = derived;
            }
        }

        // Derived measures: operations over other measures, with no cycles.
        var measureNames = root["measures"]!.AsObject().Select(kv => kv.Key).ToHashSet();
        foreach (var (name, m) in root["measures"]!.AsObject().Where(kv => kv.Value!["compute"] is not null))
        {
            var p = $"$.measures.{name}.compute";
            if (attrs.ContainsKey(name))
                errors.Add(Err("duplicate_member", $"$.measures.{name}", $"{name} is both a dimension and a measure."));
            var spec = ParseCompute(m!["compute"]!, p, measureNames, errors);
            if (spec is null) continue;
            doc.Measures[name] = new MeasureDef
            {
                Type = m["type"]!.GetValue<string>(), Derived = spec, Synonyms = Strings(m["synonyms"]), Format = m["format"]?.DeepClone() as JsonObject,
                // Evaluated again at every grain, so it carries no rollup rule of its own.
                Semantics = new Dictionary<string, object>
                {
                    ["rollup"] = new Dictionary<object, object> { ["default"] = "none" }, ["spread"] = "blank", ["status"] = "governed",
                },
            };
        }
        foreach (var name in doc.Measures.Where(kv => kv.Value.Derived is not null).Select(kv => kv.Key))
            if (Reaches(doc, name, name, []))
                errors.Add(Err("compute_cycle", $"$.measures.{name}.compute", $"{name} is defined in terms of itself."));

        foreach (var (name, m) in root["measures"]!.AsObject())
        {
            if (m!["compute"] is not null) continue;
            if (!sources.ContainsKey(m!["source"]!.GetValue<string>())) continue;
            if (!factsOf.ContainsKey(m["source"]!.GetValue<string>())) continue;
            var def = new MeasureDef
            {
                Type = m["type"]!.GetValue<string>(), Source = SourceId(m["source"]!.GetValue<string>(), m["of"]!["table"]!.GetValue<string>()), Agg = m["agg"]!.GetValue<string>(),
                Column = AnsiExpression(ColumnDef(m["of"]!["table"]!.GetValue<string>(), m["of"]!["column"]!.GetValue<string>()) ?? new JsonObject
                    { ["expression"] = new JsonObject { ["dialects"] = new JsonArray() } }) ?? "",
                Dax = m["native"]?["DAX"]?.GetValue<string>(), DaxFact = m["nonempty"]?["DAX"]?.GetValue<string>() ?? DaxTableOf(m["of"]!["table"]!.GetValue<string>(), m["of"]!["column"]!.GetValue<string>()), Synonyms = Strings(m["synonyms"]), Format = m["format"]?.DeepClone() as JsonObject,
                Roles = m["roles"]?.AsObject().ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>()) ?? [],
                Filters = m["filters"]?.AsArray().Select(f => Filter.Parse(f!)).ToList() ?? [],
            };
            if (m["semantics"] is JsonObject sem)
                foreach (var (k, v) in sem) def.Semantics[k] = Plain(v)!;
            // Rollup rules name dimensions, and first and last only make sense along time.
            if (def.Semantics.TryGetValue("rollup", out var r) && r is Dictionary<object, object> rules)
                foreach (var (dimObj, ruleObj) in rules)
                {
                    var attr = (string)dimObj;
                    if (attr == "default") continue;
                    var p = $"$.measures.{name}.semantics.rollup.{attr}";
                    if (!attrs.ContainsKey(attr))
                        errors.Add(Err("unknown_rollup_attribute", p, $"{attr} isn't a dimension."));
                    else if ((string)ruleObj is "first" or "last" && !IsTime(attrs, byName, attr))
                        errors.Add(Err("rollup_first_last_not_time", p, $"{ruleObj} needs a time dimension, and {attr} isn't marked is_time on any bound colName."));
                }
            foreach (var (owner, role) in def.Roles)
                if (!doc.Attributes.Keys.Any(k => k.StartsWith(role + ".") && doc.Attributes[k].Instance is { } inst && inst.Base.StartsWith(owner + ".")))
                    errors.Add(Err("unknown_role", $"$.measures.{name}.roles.{owner}", $"{role} isn't a role of {owner}.",
                        doc.Attributes.Values.Where(x => x.Instance is { } i && i.Base.StartsWith(owner + ".")).Select(x => x.Instance!.Value.Role).Distinct().ToList()));
            if (def.Format is { } fmt && fmt["style"]?.GetValue<string>() == "currency" && fmt["currency"] is null)
                errors.Add(Err("format_currency_missing", $"$.measures.{name}.format", $"{name} has the currency style and no currency."));
            doc.Measures[name] = def;
        }

        foreach (var (name, h) in root["hierarchies"]?.AsObject() ?? [])
            foreach (var level in Strings(h!["levels"]))
                if (!attrs.ContainsKey(level))
                    errors.Add(Err("hierarchy_unknown_level", $"$.hierarchies.{name}.levels", $"{level} isn't a dimension."));
        foreach (var (name, h) in root["hierarchies"]?.AsObject() ?? [])
            doc.Hierarchies[name] = new HierarchyDef { Levels = Strings(h!["levels"]) };

        foreach (var (name, seg) in root["segments"]?.AsObject() ?? [])
        {
            var filters = seg!["filters"]!.AsArray().Select(f => Filter.Parse(f!)).ToList();
            foreach (var member in filters.SelectMany(f => f.Names()).Distinct())
                if (!attrs.ContainsKey(member))
                    errors.Add(Err("segment_unknown_member", $"$.segments.{name}", $"{member} isn't a dimension."));
            foreach (var (leaf, leafPath) in Validator.Leaves(filters, $"$.segments.{name}.filters"))
                if (Validator.RelativeOps.Contains(leaf.Operator))
                    errors.Add(Err("segment_relative_period", leafPath + ".operator", $"{leaf.Operator} is relative to today, which a segment can't be: a request says it."));
            doc.Segments[name] = filters;
        }

        foreach (var (name, q) in root["queries"]?.AsObject() ?? [])
        {
            var def = new SavedQueryDef { Description = q!["description"]?.GetValue<string>(), Sql = q["sql"]?.GetValue<string>(), Request = AsWritten(q["request"]) as JsonObject, Parameters = AsWritten(q["parameters"]) as JsonObject ?? new JsonObject() };
            if (def.Sql is not null)
            {
                var translation = Sql.SemanticSqlTranslator.Translate(def.Sql, root["name"]?.GetValue<string>());
                if (!translation.Ok) errors.Add(Err("query_invalid", $"$.queries.{name}.sql", string.Join("; ", translation.Errors.Select(e => e.Message))));
            }
            else if (def.Request is not null && def.Request["kind"]?.GetValue<string>() != "query")
                errors.Add(Err("query_invalid", $"$.queries.{name}.request", "A saved query's request is of kind query."));
            doc.Queries[name] = def;
        }

        // A calendar names, for each time dimension, the dimension holding its member one unit earlier.
        foreach (var (calendar, c) in root["calendars"]?.AsObject() ?? [])
        {
            if (c!["date"] is { } dateNode)
            {
                var dimensions = attrs;
                var date = dateNode.GetValue<string>();
                var p = $"$.calendars.{calendar}";
                var def = new CalendarDef { Date = date };
                if (!dimensions.ContainsKey(date))
                    errors.Add(Err("calendar_unknown_attribute", p + ".date", $"{date} isn't a dimension."));
                else if (dimensions[date]!["type"]!.GetValue<string>() != "date")
                    errors.Add(Err("calendar_not_date", p + ".date", $"{date} has type {dimensions[date]!["type"]}; a calendar's date dimension has type date."));
                foreach (var (level, attr) in c["levels"]!.AsObject())
                {
                    var target = attr!.GetValue<string>();
                    if (!dimensions.ContainsKey(target))
                        errors.Add(Err("calendar_unknown_attribute", $"{p}.levels.{level}", $"{target} isn't a dimension."));
                    else def.Levels[level] = target;
                }
                if (doc.Calendars.Values.Any(o => o.Date == date))
                    errors.Add(Err("calendar_conflict", p + ".date", $"{date} is already the date dimension of another calendar."));
                doc.Calendars[calendar] = def;
            }
            foreach (var (unit, map) in c["shifts"]?.AsObject() ?? [])
                foreach (var (attr, shifted) in map!.AsObject())
                {
                    var p = $"$.calendars.{calendar}.shifts.{unit}.{attr}";
                    var dimensions = attrs;
                    var target = shifted!.GetValue<string>();
                    if (!dimensions.ContainsKey(attr) || !dimensions.ContainsKey(target))
                        errors.Add(Err("calendar_unknown_attribute", p, $"{attr} and {target} must both be dimensions."));
                    else if (!IsTime(attrs, byName, attr))
                        errors.Add(Err("calendar_not_time", p, $"{attr} isn't a time dimension: no bound colName is marked is_time."));
                    else
                    {
                        if (!doc.Shifts.TryGetValue(unit, out var units)) doc.Shifts[unit] = units = new Dictionary<string, string>();
                        if (!units.TryAdd(attr, target))
                            errors.Add(Err("calendar_conflict", p, $"{attr} already has an offset for {unit}."));
                    }
                }
        }

        // A role of a time dimension moves back through the same role of its counterpart: ship_date.fiscal_month to ship_date.fiscal_month_prior_year.
        foreach (var units in doc.Shifts.Values)
            foreach (var (attr, target) in units.ToList())
                foreach (var (instanceName, inst) in doc.Attributes.Where(kv => kv.Value.Instance is { } i && i.Base == attr).ToList())
                {
                    var counterpart = $"{inst.Instance!.Value.Role}.{target[(target.IndexOf('.') + 1)..]}";
                    if (doc.Attributes.ContainsKey(counterpart) && target.Split('.')[0] == attr.Split('.')[0]) units.TryAdd(instanceName, counterpart);
                }

        // The same for a date dimension's attributes read through a datetime.
        foreach (var units in doc.Shifts.Values)
            foreach (var (attr, target) in units.ToList())
                foreach (var (derivedName, derived) in doc.Attributes.Where(kv => kv.Value.Linked is { } l && l.Attribute == attr).ToList())
                {
                    var counterpart = $"{derived.Linked!.Value.Datetime}_{target[(target.IndexOf('.') + 1)..]}";
                    if (doc.Attributes.ContainsKey(counterpart)) units.TryAdd(derivedName, counterpart);
                }
        return doc;
    }

    private static ComputeSpec? ParseCompute(JsonNode node, string path, HashSet<string> measures, List<QisError> errors)
    {
        var op = node["op"]!.GetValue<string>();
        var args = new List<ComputeArg>();
        var ok = true;
        var raw = node["args"]!.AsArray();
        for (int i = 0; i < raw.Count; i++)
        {
            var a = raw[i]!;
            if (a.GetValueKind() == System.Text.Json.JsonValueKind.String)
            {
                var measure = a.GetValue<string>();
                if (!measures.Contains(measure)) { errors.Add(Err("compute_unknown_measure", $"{path}.args[{i}]", $"{measure} isn't a measure.")); ok = false; }
                args.Add(new ComputeArg(measure, null, null));
            }
            else if (a.GetValueKind() == System.Text.Json.JsonValueKind.Number) args.Add(new ComputeArg(null, a.GetValue<decimal>(), null));
            else
            {
                var nested = ParseCompute(a, $"{path}.args[{i}]", measures, errors);
                if (nested is null) ok = false; else args.Add(new ComputeArg(null, null, nested));
            }
        }
        if (op is "divide" or "subtract" && args.Count != 2)
        { errors.Add(Err("compute_arity", path, $"{op} takes exactly two operands.")); ok = false; }
        return ok ? new ComputeSpec(op, args) : null;
    }

    /// <summary>True if <paramref name="from"/> reads <paramref name="target"/>, directly or through other derived measures.</summary>
    private static bool Reaches(CatalogDoc doc, string from, string target, HashSet<string> seen)
    {
        if (!doc.Measures.TryGetValue(from, out var def) || def.Derived is null) return false;
        foreach (var m in def.Derived.Measures())
        {
            if (m == target) return true;
            if (seen.Add(m) && Reaches(doc, m, target, seen)) return true;
        }
        return false;
    }

    private static bool IsTime(JsonObject attrs, Dictionary<string, JsonObject> byName, string dimension)
    {
        foreach (var (_, b) in attrs[dimension]!["bindings"]!.AsObject())
        {
            var f = byName.GetValueOrDefault(b!["table"]!.GetValue<string>())?["columns"]?.AsArray()
                .FirstOrDefault(x => x!["name"]!.GetValue<string>() == b["column"]!.GetValue<string>());
            if (f is null) continue;
            var explicitTime = f["is_time"]?.GetValue<bool>();
            var datatype = f["datatype"]?.GetValue<string>();
            if (explicitTime ?? datatype is "Date" or "Time" or "DateTime" or "DateTimeTz") return true;
        }
        return false;
    }

    private static List<string> Strings(JsonNode? n) => n?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];

    private static string NextAlias(string table, HashSet<string> used)
    {
        var name = Regex.Replace(table, "^(dim|fact)_", "");
        var alias = char.ToLowerInvariant(name[0]).ToString();
        for (int i = 2; !used.Add(alias); i++) alias = char.ToLowerInvariant(name[0]) + i.ToString();
        return alias;
    }
}
