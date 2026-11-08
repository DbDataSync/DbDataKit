using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Views;

/// <summary>A document's parameter, as J25 defines one. <see cref="Default"/> is the default as JSON (a list for <c>many</c>), null when there is none.</summary>
public sealed record ViewParameter(string Name, string Type, string? For, bool Many, bool Required, bool HasDefault, string? Label, int Line, System.Text.Json.Nodes.JsonNode? Default = null);

/// <summary>A field a widget encodes (<c>x</c> of a chart, a matrix's <c>rows</c>, a metric's <c>value</c>...), by channel, in encoding order, with its path in the document (<c>widgets.by_region.encodings.x</c>) and its position.</summary>
public sealed record EncodedField(string Channel, string Field, string Path, int Line, int Column);

/// <summary>
/// A widget's query. <see cref="Form"/> is <c>derived</c> (the request is derived from the encodings), <c>semantic</c> (a request), <c>semantic_sql</c>, <c>saved</c>, <c>store</c> or <c>ref</c> (an already
/// run result); <see cref="Explicit"/> says the encodings do not drive it. <see cref="Raw"/> is the query as written, for the code that derives and runs it.
/// </summary>
public sealed record WidgetQuery(string Form, bool Explicit, string? Model, JsonObject Raw);

/// <summary>A widget: a type, its encodings and its query. <see cref="Raw"/> is the whole widget as JSON, in the order written.</summary>
public sealed record ViewWidget(string Id, string Type, string? Title, IReadOnlyList<EncodedField> Fields, WidgetQuery? Query, JsonObject Raw, int Line, int Column);

/// <summary>The result of reading a widgets map alone: the widgets, or null when there is an error, and every diagnostic.</summary>
public sealed class WidgetsLoadResult(IReadOnlyList<ViewWidget>? widgets, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public IReadOnlyList<ViewWidget>? Widgets { get; } = widgets;
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
    public bool Ok => Widgets is not null && !Diagnostics.HasErrors();
}
