using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Views;

/// <summary>One thing the engine's request validator says is wrong with a request: its code (<c>unknown_member</c>, <c>wrong_member_kind</c>, <c>order_member_missing</c>...), the place in the request (<c>$.query.measures[1]</c>) and its words.</summary>
public sealed record RequestProblem(string Code, string Path, string Message);

/// <summary>
/// The engine's validator, as the catalog check sees it: a request goes in, the problems come out. The semantic library's <c>Validator.ValidateRequest</c> is the implementation (the host adapts it, so this
/// library does not carry the engine); a test double can be a lambda.
/// </summary>
public interface IRequestValidator
{
    IReadOnlyList<RequestProblem> Validate(JsonObject request);
}

/// <summary>A semantic model as the catalog check needs it: the roles <see cref="DeriveRequest"/> reads and the validator of the model's catalog.</summary>
public sealed record CatalogBinding(CatalogRoles Roles, IRequestValidator Validator);

/// <summary>
/// The catalog <b>binding check</b> of plan 32 section 2.3, second level (DBR03): at publish and at <c>app validate</c>, every widget's request is made (a derived query by <see cref="DeriveRequest"/>, a
/// written <c>semantic.request</c> as it is) and sent through the engine's validator against the model version the document depends on. An encoded field the model lacks (<c>unknown_member</c>), a dimension in
/// a measure well (<c>wrong_member_kind</c>), a filter the model cannot take and every other server code come back as a diagnostic with <b>the engine's code</b> and the document's file, line and path. The
/// first level, that an explicit query outputs the fields it encodes, is the loader's (<c>DBD-VIEW-005</c>). A written semantic SQL, a saved query, a store read and a ref are the engine's to check when they
/// run; this check does not look inside them.
/// </summary>
public static class CatalogCheck
{
    public static IReadOnlyList<ConfigDiagnostic> Check(IEnumerable<ViewWidget> widgets, string file, IReadOnlyDictionary<string, CatalogBinding> catalogs)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        foreach (var widget in widgets)
        {
            if (widget.Query is not { } query) continue;
            var wp = $"widgets.{widget.Id}";
            if (query is { Explicit: true } and not { Form: "semantic" }) continue;
            if (query.Raw["semantic"] is JsonObject { } semantic && semantic["request"] is null) continue;
            if (query.Model is null || !catalogs.TryGetValue(query.Model, out var binding))
            {
                diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(ViewsCodes.ModelNotBound, file, widget.Line, widget.Column,
                    $"The widget's query is over the model `{query.Model}`, which the document does not depend on (it has {string.Join(", ", catalogs.Keys.Order().Select(k => $"`{k}`"))}).",
                    "Publish the model with the document, or name one it depends on.", $"{wp}.query.model"));
                continue;
            }

            JsonObject request;
            Func<string, string> place;
            if (query.Explicit)
            {
                request = (JsonObject)JsonNode.Parse(new JsonObject { ["spec_version"] = DeriveRequest.SpecVersion, ["kind"] = "query", ["query"] = query.Raw["semantic"]!["request"]!.DeepClone() }.ToJsonString())!;
                place = path => { var p = RequestPath.Normalise(path); return $"{wp}.query.semantic.request{(p.StartsWith("$.query", StringComparison.Ordinal) ? p[7..] : p)}"; };
            }
            else
            {
                var derived = DeriveRequest.Derive(widget, binding.Roles, file);
                diagnostics.AddRange(derived.Diagnostics);
                if (derived.Request is null) continue;
                request = derived.Request;
                place = derived.DocumentPath;
            }

            // the schema check lists every branch of every `oneOf` it tried: one diagnostic, the first, says where the request stopped fitting the schema
            var problems = binding.Validator.Validate(request).ToList();
            var schema = problems.Where(p => p.Code == "schema_invalid").ToList();
            if (schema.Count > 1) problems = [.. problems.Where(p => p.Code != "schema_invalid").Prepend(schema[0] with { Message = $"{schema[0].Message} (the request does not fit the semantic request schema; {schema.Count - 1} more branches of it failed)" })];
            foreach (var problem in problems)
            {
                var path = place(problem.Path);
                var at = widget.Fields.FirstOrDefault(f => f.Path == path);
                diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(problem.Code, file, at?.Line ?? widget.Line, at?.Column ?? widget.Column, problem.Message, null, path));
            }
        }
        return diagnostics;
    }
}
