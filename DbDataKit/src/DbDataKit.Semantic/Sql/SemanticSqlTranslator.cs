using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic.Sql;

/// <summary>The outcome of translating a semantic SQL text: the request, or the errors that say what was refused.</summary>
public sealed record SqlTranslation(JsonObject? Request, List<QisError> Errors)
{
    /// <summary>True when there is a request and no error.</summary>
    public bool Ok => Request is not null && Errors.Count == 0;
}

/// <summary>
/// Semantic SQL to a semantic request (docs/semantic/semantic-sql.md). DuckDB's parser reads the text; every accepted query is one request, and anything
/// outside the subset is refused with a named code (<c>sql_syntax</c>, <c>sql_unsupported</c>, <c>sql_group_by_mismatch</c>, <c>sql_wrong_model</c>), never approximated.
/// </summary>
public static class SemanticSqlTranslator
{
    private static readonly HashSet<string> AggregateFunctions = ["sum", "avg", "min", "max", "count", "median"];

    private sealed class Refused(string code, string message) : Exception(message) { public string Code { get; } = code; }

    private static Refused Unsupported(string what) => new("sql_unsupported", what);

    /// <summary>Translates the text. With <paramref name="model"/>, the name after FROM must be it (<c>sql_wrong_model</c>).</summary>
    public static SqlTranslation Translate(string sql, string? model = null, JsonObject? parameters = null)
    {
        try
        {
            var request = Build(SqlParser.ParseSelect(SqlMarkers.Rewrite(sql)), model);
            // markers become {"param": name} nodes (a positional one is named "1", "2", ...); the values travel beside the query, as in the JSON form
            if (parameters is { Count: > 0 }) request["parameters"] = parameters.DeepClone();
            return new SqlTranslation(request, []);
        }
        catch (SqlSyntaxException e) { return Failed("sql_syntax", e.Message); }
        catch (Refused e) { return Failed(e.Code, e.Message); }
    }

    private static SqlTranslation Failed(string code, string message) => new(null, [new QisError("semantic_sql", code, "$", message)]);

    // ---------- the statement ----------

    private static JsonObject Build(JsonObject node, string? model)
    {
        if (node["type"]?.GetValue<string>() != "SELECT_NODE") throw Unsupported("only a plain SELECT is a semantic query (no set operations)");
        if (node["cte_map"]?["map"] is JsonArray { Count: > 0 }) throw Unsupported("WITH is not part of a semantic query");
        if (node["sample"] is not null) throw Unsupported("SAMPLE is not part of a semantic query");

        var request = new JsonObject { ["spec_version"] = QisEngine.SpecVersion, ["kind"] = "query" };
        var query = new JsonObject();

        // FROM <model>(options)
        var options = new JsonObject();
        From(node["from_table"], model, options);

        // SELECT: attributes, then the measure entries
        var attributes = new JsonArray();
        var measures = new JsonArray();
        foreach (var item in node["select_list"]!.AsArray()) Select(item!.AsObject(), attributes, measures);
        query["measures"] = measures;
        if (attributes.Count > 0) query["attributes"] = attributes;

        var filters = new List<JsonNode>();
        if (node["where_clause"] is JsonObject where) filters.AddRange(Conjuncts(where));
        if (node["having"] is JsonObject having) filters.AddRange(Conjuncts(having));
        if (filters.Count > 0) query["filters"] = new JsonArray(filters.ToArray());

        // DISTINCT, OFFSET and the like are refused before the grouping is looked at
        Modifiers(node["modifiers"]!.AsArray(), query);
        var attributeNames = attributes.Select(a => a!.GetValue<string>()).ToList();
        GroupBy(node, attributeNames, query, options);
        if (node["qualify"] is JsonObject qualify) query["post_filters"] = new JsonArray(Conjuncts(qualify).ToArray());

        // the options: where each belongs
        foreach (var (key, value) in options)
        {
            if (key == "assumptions") request[key] = value?.DeepClone();
            else query[key] = value?.DeepClone();
        }
        request["query"] = query;
        return request;
    }

    private static void From(JsonNode? from, string? model, JsonObject options)
    {
        var type = from?["type"]?.GetValue<string>();
        string name;
        if (type == "BASE_TABLE")
        {
            if (!string.IsNullOrEmpty(from!["schema_name"]?.GetValue<string>()) || !string.IsNullOrEmpty(from["catalog_name"]?.GetValue<string>()))
                throw Unsupported("FROM names the semantic model alone, not a schema or catalog");
            name = from["table_name"]!.GetValue<string>();
        }
        else if (type == "TABLE_FUNCTION")
        {
            var function = from!["function"]!.AsObject();
            name = function["function_name"]!.GetValue<string>();
            foreach (var (key, expression) in Arguments(function))
            {
                if (string.IsNullOrEmpty(key)) throw Unsupported("the options after the model name are named: timezone := 'UTC'");
                options[key] = Value(expression);
            }
        }
        else throw Unsupported("FROM names one semantic model (no joins, subqueries or table functions)");
        if (model is not null && !string.Equals(name, model, StringComparison.OrdinalIgnoreCase))
            throw new Refused("sql_wrong_model", $"FROM names '{name}', the semantic model here is '{model}'");
    }

    // ---------- the select list ----------

    /// <summary>The arguments of a function node, as (name, expression); the name is empty for a positional one.</summary>
    private static List<(string Name, JsonNode Expression)> Arguments(JsonNode function) =>
        function["arguments"]!.AsArray().Select(a => (a!["name"]?.GetValue<string>() ?? "", a["expression"]!)).ToList();

    private static string DottedName(JsonNode refNode) => string.Join('.', refNode["column_names"]!.AsArray().Select(n => n!.GetValue<string>()));

    private static void Select(JsonObject item, JsonArray attributes, JsonArray measures)
    {
        var alias = item["alias"]?.GetValue<string>();
        switch (item["type"]?.GetValue<string>())
        {
            case "COLUMN_REF":
                if (!string.IsNullOrEmpty(alias)) throw Unsupported("an attribute can't be renamed in a semantic query");
                attributes.Add(DottedName(item));
                return;
            case "FUNCTION":
                measures.Add(MeasureEntry(item, alias));
                return;
            case "STAR": throw Unsupported("* is not part of a semantic query: name the attributes and measures");
            case "WINDOW" or "WINDOW_AGGREGATE": throw Unsupported("a window function is not part of a semantic query: use a transform such as rank(MEASURE(m))");
            default: throw Unsupported("a SELECT item must be an attribute, MEASURE(...), a transform or an aggregate of a column");
        }
    }

    private static JsonNode MeasureEntry(JsonObject f, string? alias)
    {
        var name = f["function_name"]!.GetValue<string>().ToLowerInvariant();
        var arguments = Arguments(f);
        var positional = arguments.Where(x => x.Name.Length == 0).Select(x => x.Expression).ToList();
        var named = arguments.Where(x => x.Name.Length > 0).ToList();
        if (f["distinct"]?.GetValue<bool>() == true && name != "count") throw Unsupported("DISTINCT is for count only");
        var entry = new JsonObject();

        if (name == "measure")
        {
            if (positional.Count != 1 || positional[0]["type"]?.GetValue<string>() != "COLUMN_REF") throw Unsupported("MEASURE takes one measure name");
            entry["measure"] = DottedName(positional[0]);
            foreach (var (key, expression) in named) entry[key] = Value(expression);
            if (f["filter"] is JsonObject filter) entry["filters"] = new JsonArray(Conjuncts(filter).ToArray());
            if (!string.IsNullOrEmpty(alias)) entry["as"] = alias;
            return entry.Count == 1 ? entry["measure"]!.DeepClone() : entry;
        }

        if (f["filter"] is not null) throw Unsupported("FILTER belongs to MEASURE(...)");
        if (AggregateFunctions.Contains(name) && named.Count == 0 && positional.Count == 1 && positional[0]["type"]?.GetValue<string>() == "COLUMN_REF")
        {
            entry["as"] = string.IsNullOrEmpty(alias) ? throw Unsupported("an aggregate of a column needs a name: AS ...") : alias;
            entry["agg"] = name == "count" && f["distinct"]?.GetValue<bool>() == true ? "count_distinct" : name;
            entry["of"] = DottedName(positional[0]);
            return entry;
        }

        // a transform: kind(measure or earlier alias, named arguments) AS alias
        if (positional.Count != 1 || positional[0]["type"]?.GetValue<string>() is not ("COLUMN_REF" or "FUNCTION"))
            throw Unsupported($"{name}(...) is not a transform: a transform reads one measure, MEASURE(m) or the alias of an earlier entry");
        entry["transform"] = name;
        entry["measure"] = positional[0]["type"]!.GetValue<string>() == "FUNCTION" ? MeasureOf(positional[0].AsObject()) : DottedName(positional[0]);
        foreach (var (key, expression) in named) entry[key] = Value(expression);
        if (!string.IsNullOrEmpty(alias)) entry["as"] = alias;
        return entry;
    }

    private static string MeasureOf(JsonObject f)
    {
        var arguments = f["type"]?.GetValue<string>() == "FUNCTION" && f["function_name"]?.GetValue<string>().ToLowerInvariant() == "measure" ? Arguments(f) : [];
        if (arguments.Count != 1 || arguments[0].Name.Length > 0 || arguments[0].Expression["type"]?.GetValue<string>() != "COLUMN_REF")
            throw Unsupported("a transform reads MEASURE(name) or the alias of an earlier entry");
        return DottedName(arguments[0].Expression);
    }

    // ---------- filters ----------

    private static IEnumerable<JsonNode> Conjuncts(JsonObject e)
    {
        if (e["type"]?.GetValue<string>() == "CONJUNCTION_AND") return e["children"]!.AsArray().SelectMany(c => Conjuncts(c!.AsObject()));
        return [Filter(e)];
    }

    private static JsonNode Filter(JsonObject e)
    {
        var type = e["type"]!.GetValue<string>();
        switch (type)
        {
            case "CONJUNCTION_AND": return new JsonObject { ["and"] = new JsonArray(e["children"]!.AsArray().Select(c => Filter(c!.AsObject())).ToArray()) };
            case "CONJUNCTION_OR": return new JsonObject { ["or"] = new JsonArray(e["children"]!.AsArray().Select(c => Filter(c!.AsObject())).ToArray()) };
            case "COMPARE_EQUAL": return Compare(e, "equals");
            case "COMPARE_NOTEQUAL": return Compare(e, "not_equals");
            case "COMPARE_GREATERTHAN": return Compare(e, "gt");
            case "COMPARE_GREATERTHANOREQUALTO": return Compare(e, "gte");
            case "COMPARE_LESSTHAN": return Compare(e, "lt");
            case "COMPARE_LESSTHANOREQUALTO": return Compare(e, "lte");
            case "COMPARE_IN" or "COMPARE_NOT_IN":
            {
                var children = e["children"]!.AsArray();
                var leaf = Subject(children[0]!.AsObject());
                leaf["operator"] = type == "COMPARE_IN" ? "equals" : "not_equals";
                var members = children.Skip(1).Select(c => Value(c!)!).ToArray();
                // IN :regions is the whole of the values, which a list parameter fills (a single marker in the list)
                leaf["values"] = members.Length == 1 && members[0] is JsonObject { Count: 1 } only && only["param"] is not null ? only.DeepClone() : new JsonArray(members);
                return leaf;
            }
            case "COMPARE_BETWEEN":
            {
                var leaf = Subject(e["input"]!.AsObject());
                leaf["operator"] = "between";
                leaf["values"] = new JsonArray(Value(e["lower"]!), Value(e["upper"]!));
                return leaf;
            }
            case "OPERATOR_IS_NULL" or "OPERATOR_IS_NOT_NULL":
            {
                var leaf = Subject(e["children"]![0]!.AsObject());
                leaf["operator"] = type == "OPERATOR_IS_NULL" ? "not_set" : "set";
                return leaf;
            }
            case "OPERATOR_NOT":
            {
                var inner = e["children"]![0]!.AsObject();
                if (inner["type"]?.GetValue<string>() == "FUNCTION" && inner["function_name"]?.GetValue<string>() == "contains")
                {
                    var leaf = (JsonObject)Filter(inner);
                    leaf["operator"] = "not_contains";
                    return leaf;
                }
                throw Unsupported("NOT is written with the negated operator: <>, NOT IN, IS NOT NULL, NOT contains(...)");
            }
            case "FUNCTION": return FunctionFilter(e);
            default: throw Unsupported($"the condition ({type}) has no form in a semantic query");
        }
    }

    private static JsonObject Subject(JsonObject e)
    {
        var leaf = new JsonObject();
        switch (e["type"]?.GetValue<string>())
        {
            case "COLUMN_REF":
            {
                var name = DottedName(e);
                // a dotted name is an attribute; a plain word is the alias of a transform (a condition on its output)
                leaf[name.Contains('.') ? "attribute" : "measure"] = name;
                return leaf;
            }
            case "FUNCTION" when e["function_name"]?.GetValue<string>().ToLowerInvariant() == "measure":
                leaf["measure"] = MeasureOf(e);
                return leaf;
            default: throw Unsupported("a condition compares an attribute, MEASURE(m) or a transform's alias with a value (no expressions)");
        }
    }

    private static JsonObject Compare(JsonObject e, string op)
    {
        var leaf = Subject(e["left"]!.AsObject());
        leaf["operator"] = op;
        leaf["values"] = new JsonArray(Value(e["right"]!)!);
        return leaf;
    }

    private static JsonNode FunctionFilter(JsonObject e)
    {
        var name = e["function_name"]!.GetValue<string>().ToLowerInvariant();
        var arguments = Arguments(e);
        if (arguments.Count == 0 || arguments[0].Name.Length > 0) throw Unsupported($"{name}() needs the attribute it applies to, then its values");
        var subject = arguments[0].Expression.AsObject();
        if (name == "operator")
        {
            // operator(attribute, 'name', values..., count := n): an operator written by name, for one with no form of its own
            var positional = arguments.Where(x => x.Name.Length == 0).ToList();
            if (positional.Count < 2 || Value(positional[1].Expression) is not JsonValue opName || !opName.TryGetValue<string>(out var op))
                throw Unsupported("operator(attribute, 'name', values...)");
            var leaf = Subject(subject);
            leaf["operator"] = op;
            if (positional.Count > 2) leaf["values"] = new JsonArray(positional.Skip(2).Select(x => Value(x.Expression)!).ToArray());
            foreach (var (key, expression) in arguments.Where(x => x.Name.Length > 0))
                leaf[key] = key == "count" ? Value(expression) : throw Unsupported($"operator(...) takes no argument named {key}");
            return leaf;
        }
        if (name is "contains" or "starts_with")
        {
            if (arguments.Count < 2) throw Unsupported($"{name} needs a value");
            var leaf = Subject(subject);
            leaf["operator"] = name;
            leaf["values"] = new JsonArray(arguments.Skip(1).Select(x => Value(x.Expression)!).ToArray());
            return leaf;
        }
        if (name is "last_complete" or "current" or "period_to_date")
        {
            var leaf = Subject(subject);
            leaf["operator"] = name;
            if (name == "last_complete")
            {
                if (arguments.Count != 2) throw Unsupported("last_complete(attribute, count)");
                leaf["count"] = Value(arguments[1].Expression);
            }
            else if (arguments.Count != 1) throw Unsupported($"{name}(attribute)");
            return leaf;
        }
        throw Unsupported($"the function {name}() is not a condition of a semantic query (contains, starts_with, last_complete, current, period_to_date, operator)");
    }

    // ---------- group by, order, limit ----------

    private static void GroupBy(JsonObject node, List<string> attributes, JsonObject query, JsonObject options)
    {
        var expressions = node["group_expressions"]!.AsArray();
        var sets = node["group_sets"]!.AsArray();
        var handling = node["aggregate_handling"]?.GetValue<string>();
        if (attributes.Count == 0)
        {
            if (expressions.Count > 0 || handling == "FORCE_AGGREGATES") throw new Refused("sql_group_by_mismatch", "GROUP BY on a query with no attributes");
            return;
        }
        if (handling == "FORCE_AGGREGATES" && expressions.Count == 0) return;   // GROUP BY ALL
        var names = expressions.Select(x => x!["type"]?.GetValue<string>() == "COLUMN_REF" ? DottedName(x) : throw Unsupported("GROUP BY lists attributes")).ToList();
        if (names.Distinct().Count() != names.Count) throw new Refused("sql_group_by_mismatch", "an attribute is listed twice in GROUP BY");
        if (!names.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(attributes.OrderBy(x => x, StringComparer.Ordinal)))
            throw new Refused("sql_group_by_mismatch", $"GROUP BY must group by the attributes of the SELECT list ({string.Join(", ", attributes)}), or be GROUP BY ALL");

        var setList = sets.Select(s => s!.AsArray().Select(i => i!.GetValue<int>()).OrderBy(i => i).ToList()).ToList();
        if (setList.Count == 1 && setList[0].Count == names.Count) return;   // a plain grouping

        // the attributes in every set partition; the rest are rolled up, in the order they were written
        var partition = Enumerable.Range(0, names.Count).Where(i => setList.All(s => s.Contains(i))).ToList();
        var rolled = Enumerable.Range(0, names.Count).Where(i => !partition.Contains(i)).ToList();
        var subsets = setList.Select(s => s.Where(i => !partition.Contains(i)).ToList()).ToList();
        var key = (List<int> s) => string.Join(",", s);
        var have = subsets.Select(key).ToHashSet();
        bool hasEmpty = have.Contains("");

        bool cube = rolled.Count > 1 && have.Count == (1 << rolled.Count) - (hasEmpty ? 0 : 1)
            && Enumerable.Range(hasEmpty ? 0 : 1, (1 << rolled.Count) - (hasEmpty ? 0 : 1)).All(mask => have.Contains(key(rolled.Where((_, bit) => (mask & (1 << (rolled.Count - 1 - bit))) != 0).ToList())));
        bool chain = Enumerable.Range(hasEmpty ? 0 : 1, rolled.Count + 1 - (hasEmpty ? 0 : 1)).All(n => have.Contains(key(rolled.Take(n).ToList()))) && have.Count == rolled.Count + 1 - (hasEmpty ? 0 : 1);
        if (!cube && !chain) throw Unsupported("GROUP BY sets must be ROLLUP, CUBE, or the prefixes (or every subset) of a list written as GROUPING SETS");

        var totals = new JsonObject { ["subtotal_by"] = new JsonArray(rolled.Select(i => (JsonNode)names[i]).ToArray()), ["grand_total"] = hasEmpty };
        if (cube && rolled.Count > 1) totals["cube"] = true;
        query["totals"] = totals;
    }

    private static void Modifiers(JsonArray modifiers, JsonObject query)
    {
        foreach (var m in modifiers)
        {
            switch (m!["type"]?.GetValue<string>())
            {
                case "ORDER_MODIFIER":
                    var order = new JsonArray();
                    foreach (var o in m["orders"]!.AsArray())
                    {
                        var dir = o!["type"]!.GetValue<string>() == "DESCENDING" ? "desc" : "asc";
                        if (o["null_order"]?.GetValue<string>() is not (null or "ORDER_DEFAULT")) throw Unsupported("NULLS FIRST and NULLS LAST are not part of a semantic query: nulls sort as the engine says");
                        var e = o["expression"]!.AsObject();
                        string member = e["type"]?.GetValue<string>() switch
                        {
                            "COLUMN_REF" => DottedName(e),
                            "FUNCTION" => MeasureOf(e),
                            "VALUE_CONSTANT" => throw Unsupported("ORDER BY a position is not part of a semantic query: name the attribute, MEASURE(m) or alias"),
                            _ => throw Unsupported("ORDER BY names an attribute, MEASURE(m) or an alias"),
                        };
                        order.Add(new JsonArray(member, dir));
                    }
                    if (order.Count > 0) query["order"] = order;
                    break;
                case "LIMIT_MODIFIER":
                    if (m["offset"] is not null) throw Unsupported("OFFSET is not part of a semantic query");
                    query["limit"] = Value(m["limit"]!);
                    break;
                default: throw Unsupported("DISTINCT and other modifiers are not part of a semantic query");
            }
        }
    }

    // ---------- values ----------

    /// <summary>A constant, list or struct expression as the JSON value it stands for.</summary>
    private static JsonNode? Value(JsonNode e)
    {
        switch (e["type"]?.GetValue<string>())
        {
            case "VALUE_CONSTANT": return Constant(e["literal"]!.AsObject());
            case "VALUE_PARAMETER": return new JsonObject { ["param"] = e["identifier"]?.GetValue<string>() ?? throw Unsupported("a parameter marker has no name") };
            case "FUNCTION":
            {
                var name = e["function_name"]!.GetValue<string>();
                var arguments = Arguments(e);
                if (name == "list_value") return new JsonArray(arguments.Select(x => Value(x.Expression)).ToArray());
                if (name == "struct_pack")
                {
                    var o = new JsonObject();
                    foreach (var (key, expression) in arguments) o[key] = Value(expression);
                    return o;
                }
                throw Unsupported($"{name}(...) is not a value: a value is a string, a number, TRUE, FALSE, NULL, [list] or {{'key': value}}");
            }
            case "OPERATOR_CAST":
            {
                // DATE '2026-01-01' is a cast of the string; the value is the string
                var type = e["type_expr"]?["type_name"]?.GetValue<string>()?.ToUpperInvariant();
                if (type is "DATE" or "TIMESTAMP" or "TIMESTAMPTZ" or "TIMESTAMP WITH TIME ZONE") return Value(e["child"]!);
                throw Unsupported($"a cast to {type} is not a value of a semantic query");
            }
            default: throw Unsupported("a value is a string, a number, TRUE, FALSE, NULL, [list] or {'key': value}");
        }
    }

    private static JsonNode? Constant(JsonObject literal)
    {
        var text = literal["text"]?.GetValue<string>() ?? "";
        return literal["kind"]?.GetValue<string>() switch
        {
            "STRING" => JsonValue.Create(text),
            "BOOLEAN" => JsonValue.Create(string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)),
            "NULL_LITERAL" => null,
            // parsed as JSON, so the number reads as an int, a long or a decimal whichever the engine asks for, as a number from a file does
            "INTEGER" or "NUMERIC" => decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? JsonNode.Parse(text) : throw Unsupported($"'{text}' is not a number"),
            var kind => throw Unsupported($"a {kind} literal is not a value of a semantic query"),
        };
    }
}
