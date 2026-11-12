using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Views;

/// <summary>What a document is intended to be: the catalog, the create flows and the registry list and filter by it.</summary>
public enum ViewKind { Dashboard, Report }

/// <summary>The three layout types: two that fit one page and one that scrolls and splits into pages.</summary>
public enum LayoutType { Reflow, Canvas, Flow }

/// <summary>A numeric range of a selector; either end may be missing.</summary>
public sealed record ViewRange(double? Min, double? Max)
{
    public bool Contains(double value) => (Min is null || value >= Min) && (Max is null || value <= Max);
}

/// <summary>
/// The selector of a layout (<c>when</c>, plan 32 section 3.2). Conditions are ANDed and an omitted one matches anything; <see cref="Specificity"/> is the number of conditions.
/// </summary>
public sealed record LayoutWhen(ViewRange? Width, ViewRange? Height, string? Orientation, ViewRange? Aspect, IReadOnlyList<string>? Medium)
{
    public int Specificity => (Width is null ? 0 : 1) + (Height is null ? 0 : 1) + (Orientation is null ? 0 : 1) + (Aspect is null ? 0 : 1) + (Medium is null ? 0 : 1);

    /// <summary>Whether two selectors say the same thing (the later of two layouts with equal selectors is never reached).</summary>
    public bool SameAs(LayoutWhen other) =>
        Width == other.Width && Height == other.Height && Orientation == other.Orientation && Aspect == other.Aspect &&
        (Medium ?? []).Order().SequenceEqual((other.Medium ?? []).Order());
}

/// <summary>A tile of a reflow layout (cells) or a canvas layout (the canvas's unit).</summary>
public sealed record ViewTile(string Widget, double X, double Y, double W, double H, int Layer, double? MinW, double? MinH, int Line);

public sealed record ReflowGrid(int Columns, int? Rows);

/// <summary>A canvas: its size in its own unit, the unit (px, mm, in; 96 px per inch), how it fits the viewport, and the scale limits.</summary>
public sealed record CanvasSpec(double Width, double Height, string Unit, string Overflow, double? MinScale, double? MaxScale)
{
    /// <summary>Pixels per unit (CSS: 96 per inch).</summary>
    public double PixelsPerUnit => Unit switch { "mm" => 96 / 25.4, "in" => 96, _ => 1 };
    public double WidthPx => Width * PixelsPerUnit;
    public double HeightPx => Height * PixelsPerUnit;
}

/// <summary>
/// A layout: where the document's widgets go. <see cref="Placed"/> lists the widget ids it places, in document order, once each. A reflow layout has <see cref="Grid"/> and <see cref="Tiles"/>, a canvas
/// has <see cref="Canvas"/> and <see cref="Tiles"/>, and a flow layout keeps its sections in <see cref="Raw"/> (the flow engine of DBR14 reads it).
/// </summary>
public sealed record ViewLayout(string Name, LayoutType Type, bool IsDefault, LayoutWhen? When, IReadOnlyList<string> Placed, ReflowGrid? Grid, CanvasSpec? Canvas, IReadOnlyList<ViewTile> Tiles, JsonObject Raw, int Line, int Column);

/// <summary>A loaded dashboard or report. The kind's own keys are null for the other kind.</summary>
public sealed record ViewDocument(
    ViewKind Kind,
    string Title,
    IReadOnlyList<ViewParameter> Parameters,
    IReadOnlyList<ViewWidget> Widgets,
    IReadOnlyList<ViewLayout> Layouts,
    string? Font,
    string? Refresh,
    string? KioskLayout,
    bool? Strict,
    int? MaxPages,
    int? MaxRows,
    bool? CoverParameters,
    string? Locale = null,
    IReadOnlyDictionary<string, ViewLink>? LinkMap = null,
    IReadOnlyDictionary<string, ViewGroup>? GroupMap = null)
{
    /// <summary>The field groups the document declares (<c>groups</c>), by name; empty when it has none.</summary>
    public IReadOnlyDictionary<string, ViewGroup> Groups => GroupMap ?? EmptyGroups;

    private static readonly IReadOnlyDictionary<string, ViewGroup> EmptyGroups = new Dictionary<string, ViewGroup>();

    /// <summary>The links the document declares (<c>links</c>), by name; empty when it has none.</summary>
    public IReadOnlyDictionary<string, ViewLink> Links => LinkMap ?? EmptyLinks;

    private static readonly IReadOnlyDictionary<string, ViewLink> EmptyLinks = new Dictionary<string, ViewLink>();

    public ViewLayout DefaultLayout => Layouts.First(l => l.IsDefault);

    public ViewWidget? Widget(string id) => Widgets.FirstOrDefault(w => w.Id == id);
}

/// <summary>The result of loading a document: the document, or null when there is an error, and every diagnostic (errors and warnings).</summary>
public sealed class ViewsLoadResult(ViewDocument? document, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public ViewDocument? Document { get; } = document;
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
    public bool Ok => Document is not null && !Diagnostics.HasErrors();
}
