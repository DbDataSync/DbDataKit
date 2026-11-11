namespace DbDataKit.Apps;

/// <summary>A position in a spec file, for diagnostics that come after loading (a check against the models, a link to another view).</summary>
public readonly record struct SpecPos(int Line, int Column);

/// <summary>What a view spec shows (<c>view-spec/1</c>): a list of rows, a form to create or edit one row, one row's page, or a page of figures.</summary>
public enum ViewKind { Grid, Form, Detail, Dashboard }

public enum ViewParamType { String, Int, Bool, Date }

public enum ViewSortDirection { Asc, Desc }

public enum ViewFormMode { Create, Edit }

public enum ViewWidgetKind { Metric, Bar, Line, Table }

public enum ViewAggregate { Count, Sum, Avg, Min, Max }

/// <summary>A parameter a view takes from its address or from a link to it: a name, a type, whether it must be given and a default.</summary>
public sealed record ViewParam(string Name, ViewParamType Type, bool Required, string? Default, SpecPos At);

/// <summary>Where a link parameter's value comes from: a text written in the spec, an attribute of the row the link belongs to (<c>$name</c>), or a parameter of this view (<c>$params.name</c>).</summary>
public enum ViewValueKind { Literal, Attribute, Param }

public sealed record ViewValue(ViewValueKind Kind, string Text, SpecPos At);

/// <summary>A link to another view of the app: <see cref="To"/> is that view's file in the app, <see cref="With"/> gives its parameters. <see cref="PerRow"/> puts the link on every row of a grid.</summary>
public sealed record ViewLink(string Label, string To, bool PerRow, IReadOnlyDictionary<string, ViewValue> With, SpecPos At, SpecPos ToAt);

/// <summary>An attribute of the entity, shown under a label (or its own name).</summary>
public sealed record ViewAttribute(string Name, string? Label, SpecPos At);

public sealed record ViewColumn(string Attribute, string? Label, bool Sortable, SpecPos At);

public sealed record ViewField(string Attribute, string? Label, bool ReadOnly, bool Required, string? Help, SpecPos At);

public sealed record ViewSort(string Attribute, ViewSortDirection Direction, SpecPos At);

public sealed record ViewSection(string Title, IReadOnlyList<ViewAttribute> Attributes, SpecPos At);

/// <summary>One figure of a dashboard, over one entity of the view's model: a measure (<see cref="Aggregate"/> of <see cref="Attribute"/>, or a count), grouped by <see cref="By"/> for a bar or a line, or a table of <see cref="Columns"/>.</summary>
public sealed record ViewWidget(ViewWidgetKind Kind, string Title, string Entity, ViewAggregate Aggregate, ViewAttribute? Measure, ViewAttribute? By, IReadOnlyList<ViewAttribute> Columns, int Limit, SpecPos At, SpecPos EntityAt);

/// <summary>A declarative view (<c>view-spec/1</c>), read from a file of the app named under <c>views</c> in its manifest. The file is data the host draws with its own components, never script.</summary>
public abstract record ViewSpec(ViewKind Kind, string File, string Title, string Model, IReadOnlyList<ViewParam> Params, IReadOnlyList<ViewLink> Links, SpecPos At, SpecPos ModelAt)
{
    public const string FormatId = "view-spec/1";
}

public sealed record GridView(string File, string Title, string Model, string Entity, IReadOnlyList<ViewColumn> Columns, IReadOnlyList<ViewAttribute> Filters, IReadOnlyList<ViewSort> Sort, int PageSize,
    IReadOnlyList<ViewParam> Params, IReadOnlyList<ViewLink> Links, SpecPos At, SpecPos ModelAt, SpecPos EntityAt)
    : ViewSpec(ViewKind.Grid, File, Title, Model, Params, Links, At, ModelAt);

public sealed record FormView(string File, string Title, string Model, string Entity, ViewFormMode Mode, string? Key, string? Submit, IReadOnlyList<ViewField> Fields,
    IReadOnlyList<ViewParam> Params, IReadOnlyList<ViewLink> Links, SpecPos At, SpecPos ModelAt, SpecPos EntityAt)
    : ViewSpec(ViewKind.Form, File, Title, Model, Params, Links, At, ModelAt);

public sealed record DetailView(string File, string Title, string Model, string Entity, string Key, IReadOnlyList<ViewSection> Sections,
    IReadOnlyList<ViewParam> Params, IReadOnlyList<ViewLink> Links, SpecPos At, SpecPos ModelAt, SpecPos EntityAt)
    : ViewSpec(ViewKind.Detail, File, Title, Model, Params, Links, At, ModelAt);

public sealed record DashboardView(string File, string Title, string Model, IReadOnlyList<ViewWidget> Widgets,
    IReadOnlyList<ViewParam> Params, IReadOnlyList<ViewLink> Links, SpecPos At, SpecPos ModelAt)
    : ViewSpec(ViewKind.Dashboard, File, Title, Model, Params, Links, At, ModelAt);
