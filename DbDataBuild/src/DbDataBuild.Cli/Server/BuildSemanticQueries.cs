using System.Text.Json.Nodes;
using DbDataKit.Semantic;
using DbDataKit.Semantic.Sql;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// The translation check of a model's saved query (plan 14a SL20): the text of one entry of <c>queries:</c> as the editor holds it, read by the same translator the catalog loader uses, so what the form shows at the line is what the
/// compiler will say. Nothing is run and nothing is stored. The answer is <c>{ok, errors: [{code, message}], warnings: [{code, message}], parameters: [{name, for, type, many}], request}</c>: the request is the semantic request a
/// <c>sql</c> text becomes, and the parameters are what the query asks for (a marker is one), so the form can offer a definition for each one it has none for.
/// </summary>
internal static class BuildSemanticQueries
{
    public static JsonObject Check(string modelName, Catalog catalog, JsonObject entry)
    {
        var errors = new JsonArray();
        var warnings = new JsonArray();
        void Add(JsonArray into, string code, string message) => into.Add(new JsonObject { ["code"] = code, ["message"] = message });

        var sql = entry["sql"] is JsonValue s && s.TryGetValue<string>(out var text) ? text : null;
        var request = entry["request"] as JsonObject;
        JsonObject? resolved = null;
        if ((sql is null) == (request is null)) Add(errors, "query_invalid", "A saved query has exactly one of sql and request.");
        else if (sql is not null)
        {
            if (string.IsNullOrWhiteSpace(sql)) Add(errors, "query_invalid", "The sql is empty.");
            else
            {
                var translation = SemanticSqlTranslator.Translate(sql, modelName);
                foreach (var error in translation.Errors) Add(errors, error.Code, error.Message);
                resolved = translation.Ok ? translation.Request : null;
            }
        }
        else if (request!["kind"]?.GetValue<string>() != "query") Add(errors, "query_invalid", "A saved query's request is of kind query.");
        else resolved = request;

        var shapes = resolved is null ? [] : QueryParameters.Describe(resolved, catalog);
        var defined = (entry["parameters"] as JsonObject)?.Select(p => p.Key).ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var shape in shapes.Where(p => !defined.Contains(p.Name)))
            Add(warnings, "parameter_undefined", $"The query asks for the parameter '{shape.Name}' and the parameters section does not define it (it is then required, of the type of what it is compared with).");
        if (resolved is not null)
            foreach (var name in defined.Where(n => shapes.All(p => p.Name != n)))
                Add(warnings, "parameter_unused", $"The parameters section defines '{name}' and the query never uses it.");

        return new JsonObject
        {
            ["ok"] = errors.Count == 0,
            ["errors"] = errors,
            ["warnings"] = warnings,
            ["parameters"] = new JsonArray(shapes.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["for"] = p.For, ["type"] = p.Type, ["many"] = p.Many }).ToArray()),
            ["request"] = sql is not null ? resolved?.DeepClone() : null,
        };
    }
}
