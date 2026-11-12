using System.Text.Json.Nodes;

namespace DbDataKit.Views;

/// <summary>
/// The body of a render request, read once for every host that renders a document as the viewer (the <c>views</c> module and the apps host, plan 32 DBR13): <c>parameters</c> (by document parameter name), <c>layout</c> (omitted: chosen
/// for <c>viewport</c> and <c>medium</c>, else the default), <c>only</c> (widget ids to resolve), <c>order</c> (a viewer's sort of a table the row limit cut, by widget id, DBR45) and <c>expand</c> (the rows a viewer expanded in a matrix
/// with a <c>show: drill</c> group, by widget id, DBR55).
/// </summary>
public sealed record RenderRequest(
    Dictionary<string, JsonNode?>? Parameters, List<string>? Only, string? Layout, LayoutContext Viewport,
    Dictionary<string, WidgetOrder>? Orders, Dictionary<string, IReadOnlyList<IReadOnlyList<JsonNode?>>>? Expands)
{
    /// <summary>Reads the body; a key this host does not know, or a value of the wrong shape, is an <see cref="ArgumentException"/> in words.</summary>
    public static RenderRequest Parse(JsonObject? body)
    {
        foreach (var key in body?.Select(p => p.Key) ?? [])
            if (key is not ("parameters" or "layout" or "only" or "viewport" or "medium" or "order" or "expand"))
                throw new ArgumentException($"Unknown key `{key}`: a render request has `parameters`, `layout`, `only`, `order`, `expand`, `viewport` and `medium`.");
        Dictionary<string, WidgetOrder>? orders = null;
        Dictionary<string, IReadOnlyList<IReadOnlyList<JsonNode?>>>? expands = null;
        Dictionary<string, JsonNode?>? parameters = null;
        List<string>? only = null;
        string? layout = null;
        if (body?["order"] is { } ord) orders = ReadOrders(ord);
        if (body?["expand"] is { } exp) expands = ReadExpand(exp);
        if (body?["parameters"] is { } p) parameters = (p as JsonObject ?? throw new ArgumentException("`parameters` must be an object of values by parameter name.")).ToDictionary(x => x.Key, x => x.Value?.DeepClone());
        if (body?["only"] is { } o) only = (o as JsonArray ?? throw new ArgumentException("`only` must be a list of widget ids.")).Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException("`only` must be a list of widget ids.")).ToList();
        if (body?["layout"] is { } l) layout = l is JsonValue lv && lv.TryGetValue<string>(out var ls) ? ls : throw new ArgumentException("`layout` must be a layout's name.");
        var medium = body?["medium"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : "screen";
        double? Dimension(string key) => body?["viewport"] is JsonObject v && v[key] is JsonValue d && d.TryGetValue<double>(out var n) ? n : null;
        return new RenderRequest(parameters, only, layout, new LayoutContext(Dimension("width"), Dimension("height"), medium, layout), orders, expands);
    }

    /// <summary>A render request's <c>order</c>: an object of widget id to <c>{by, dir}</c> (<c>dir</c> is <c>asc</c> or <c>desc</c>, default <c>asc</c>).</summary>
    private static Dictionary<string, WidgetOrder> ReadOrders(JsonNode node)
    {
        const string Shape = "`order` must be an object of widget ids to `{\"by\": field, \"dir\": \"asc\" | \"desc\"}`.";
        var result = new Dictionary<string, WidgetOrder>(StringComparer.Ordinal);
        foreach (var (id, value) in node as JsonObject ?? throw new ArgumentException(Shape))
        {
            if (value is not JsonObject o || o["by"] is not JsonValue by || !by.TryGetValue<string>(out var field) || field.Length == 0) throw new ArgumentException(Shape);
            var dir = o["dir"] is JsonValue d && d.TryGetValue<string>(out var text) ? text : "asc";
            if (dir is not ("asc" or "desc")) throw new ArgumentException(Shape);
            result[id] = new WidgetOrder(field, dir == "desc");
        }
        return result;
    }

    /// <summary>A render request's <c>expand</c> (plan 32 DBR55): an object of widget id to the rows expanded, each the list of the member values of the row's own fields, at most <see cref="WidgetResolver.MaximumExpansions"/> rows.</summary>
    private static Dictionary<string, IReadOnlyList<IReadOnlyList<JsonNode?>>> ReadExpand(JsonNode node)
    {
        const string Shape = "`expand` must be an object of widget ids to lists of rows, each row the list of its member values: `{\"orders\": [[\"West\"], [\"East\"]]}`.";
        var result = new Dictionary<string, IReadOnlyList<IReadOnlyList<JsonNode?>>>(StringComparer.Ordinal);
        foreach (var (id, value) in node as JsonObject ?? throw new ArgumentException(Shape))
        {
            if (value is not JsonArray rows || rows.Count > WidgetResolver.MaximumExpansions) throw new ArgumentException(Shape);
            result[id] = rows.Select(r => r is JsonArray key && key.All(v => v is null or JsonValue) ? (IReadOnlyList<JsonNode?>)key.Select(v => v?.DeepClone()).ToList() : throw new ArgumentException(Shape)).ToList();
        }
        return result;
    }
}
