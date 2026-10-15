using System.Text.Json.Nodes;

namespace DbData.Config.Testing;

/// <summary>The binder/schema agreement check (Plan 01 section 3.6): every key the binder accepts is in the generated JSON Schema, and vice versa, recursively.</summary>
public static class SchemaAgreement
{
    public static void Assert<T>() where T : class => Assert(typeof(T));

    public static void Assert(Type configType)
    {
        var problems = new List<string>();
        Check(SchemaModel.For(configType), JsonSchemaGenerator.Generate(configType), "$", true, problems);
        if (problems.Count > 0)
            throw new ConfigTestException($"JSON Schema and binder disagree for {configType.Name}:\n" + string.Join("\n", problems));
    }

    private static void Check(TypeSchema model, JsonObject json, string where, bool top, List<string> problems)
    {
        switch (model)
        {
            case ObjectSchema o:
            {
                if (json["properties"] is not JsonObject props) { problems.Add($"{where}: schema has no `properties`"); return; }
                var inSchema = props.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
                var inBinder = o.AllowedKeys(top).ToHashSet(StringComparer.Ordinal);
                foreach (var k in inBinder.Except(inSchema).Order(StringComparer.Ordinal)) problems.Add($"{where}.{k}: accepted by the binder, missing from the schema");
                foreach (var k in inSchema.Except(inBinder).Order(StringComparer.Ordinal)) problems.Add($"{where}.{k}: in the schema, rejected by the binder");
                var preserve = o.UnknownKeys == UnknownKeyPolicy.Preserve;
                if (json["additionalProperties"] is not JsonValue ap || ap.GetValue<bool>() != preserve)
                    problems.Add($"{where}: additionalProperties should be {preserve.ToString().ToLowerInvariant()} for the {o.UnknownKeys} unknown-key policy");
                foreach (var p in o.Properties)
                    if (props[p.Key] is JsonObject child) Check(p.Type, child, $"{where}.{p.Key}", false, problems);
                break;
            }
            case ListSchema l:
                if (json["items"] is JsonObject items) Check(l.Item, items, where + "[]", false, problems); else problems.Add($"{where}: list schema has no `items`");
                break;
            case MapSchema m:
                if (json["additionalProperties"] is JsonObject ap2) Check(m.Value, ap2, where + ".*", false, problems); else problems.Add($"{where}: map schema has no `additionalProperties`");
                break;
            case UnionSchema u:
            {
                if (json["oneOf"] is not JsonArray one) { problems.Add($"{where}: union schema has no `oneOf`"); return; }
                if (one.Count != u.Cases.Count) problems.Add($"{where}: oneOf has {one.Count} cases, the union has {u.Cases.Count}");
                foreach (var (name, c) in u.Cases)
                {
                    var j = one.OfType<JsonObject>().FirstOrDefault(x => x["properties"]?[u.Discriminator]?["const"]?.GetValue<string>() == name);
                    if (j is null) { problems.Add($"{where}: union case `{name}` is not in oneOf"); continue; }
                    var jp = ((JsonObject)j["properties"]!).Select(p => p.Key).Where(k => k != u.Discriminator).ToHashSet(StringComparer.Ordinal);
                    var bp = c.AllowedKeys(false).ToHashSet(StringComparer.Ordinal);
                    foreach (var k in bp.Except(jp)) problems.Add($"{where}<{name}>.{k}: accepted by the binder, missing from the schema");
                    foreach (var k in jp.Except(bp)) problems.Add($"{where}<{name}>.{k}: in the schema, rejected by the binder");
                }
                break;
            }
        }
    }
}
