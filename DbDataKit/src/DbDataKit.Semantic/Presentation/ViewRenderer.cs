using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic.Presentation;

/// <summary>
/// The presentation spec: a view document and the result it refers to become grids and chart specs, or errors. Rendering reads
/// rows and arranges them; it never computes a measure, including totals, so the same result shown two ways has the same numbers.
/// </summary>
public sealed class ViewRenderer
{
    /// <summary>
    /// Validates the view document against its schema and the result's descriptor, then renders every view. Either all views
    /// render, or none do and the errors say why. <paramref name="result"/> is a result document, or a result set with a descriptor and rows.
    /// </summary>
    public JsonObject Render(JsonObject viewDocument, JsonObject result)
    {
        var errors = SchemaStore.Errors(viewDocument, SchemaStore.Get("view"), "schema_invalid", "presentation");
        var views = new JsonArray();
        if (errors.Count == 0)
        {
            var table = new ResultTable(result);
            var specs = viewDocument["views"]!.AsArray();
            var ids = new HashSet<string>();
            for (int i = 0; i < specs.Count; i++)
            {
                var spec = specs[i]!.AsObject();
                var path = $"$.views[{i}]";
                if (!ids.Add(spec["id"]!.GetValue<string>()))
                    errors.Add(new QisError("presentation", "duplicate_view_id", path + ".id", $"Another view is already called {spec["id"]}."));
                var (view, viewErrors) = spec["type"]!.GetValue<string>() == "matrix"
                    ? MatrixView.Render(spec, table, path) : ChartView.Render(spec, table, path);
                errors.AddRange(viewErrors);
                if (view is not null) views.Add(view);
            }
        }
        return new JsonObject
        {
            ["kind"] = "rendered_view",
            ["status"] = errors.Count == 0 ? "complete" : "failed",
            ["views"] = errors.Count == 0 ? views : null,
            ["errors"] = errors.Count == 0 ? null : System.Text.Json.JsonSerializer.SerializeToNode(errors, QisJson.Options),
        };
    }
}
