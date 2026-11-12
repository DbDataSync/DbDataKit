namespace DbDataKit.Views;

/// <summary>
/// The diagnostic codes of the dashboard and report loaders (series <c>DBD-VIEW-</c>). A key that is not known, a missing key and a value of the wrong shape use the shared codes
/// <c>DBD-104</c>, <c>DBD-105</c> and <c>DBD-106</c>, and a newer format <c>DBD-110</c>; everything else has its own code so a test, an editor and an agent can tell the refusals apart.
/// The TypeScript validators of <c>@dbdatakit/visuals</c> report the same codes at the same paths.
/// </summary>
public static class ViewsCodes
{
    public const string UnknownKey = "DBD-104";
    public const string MissingKey = "DBD-105";
    public const string InvalidValue = "DBD-106";
    public const string FormatNewer = "DBD-110";
    public const string FormatInvalid = "DBD-111";

    /// <summary>The file is a dashboard where a report is read, or the other way (or neither).</summary>
    public const string WrongKind = "DBD-VIEW-001";
    /// <summary>Text with raw HTML, an image, or a link that leaves the host.</summary>
    public const string UnsafeText = "DBD-VIEW-002";
    /// <summary>An image by URL, an SVG, or anything but a raster held by hash.</summary>
    public const string UnsafeImage = "DBD-VIEW-003";
    /// <summary>A chart spec outside the Vega-Lite subset: data, a transform, an aggregate, a bin, an expression.</summary>
    public const string ChartNotInSubset = "DBD-VIEW-004";
    /// <summary>An encoded field that is not an output of the widget's explicit query.</summary>
    public const string FieldNotInQuery = "DBD-VIEW-005";
    /// <summary>A widget type the resolver does not run (it has no resolution of its own).</summary>
    public const string ReservedWidget = "DBD-VIEW-006";
    /// <summary>A query that is not one of the forms, or a mode that does not fit it.</summary>
    public const string BadQuery = "DBD-VIEW-007";
    /// <summary>A parameter that is not declared (a marker, a control, a cross-filter, a drill).</summary>
    public const string UndeclaredParameter = "DBD-VIEW-008";
    /// <summary>A name used twice (widgets are a map; layouts and parameters are lists).</summary>
    public const string DuplicateName = "DBD-VIEW-020";
    /// <summary>Not exactly one layout says <c>default: true</c>.</summary>
    public const string DefaultLayoutCount = "DBD-VIEW-021";
    /// <summary>The default layout does not place every widget.</summary>
    public const string DefaultMissesWidget = "DBD-VIEW-022";
    /// <summary>A widget that no layout places.</summary>
    public const string DeadWidget = "DBD-VIEW-023";
    /// <summary>A key of another layout type (a canvas key in a reflow layout).</summary>
    public const string KeyOfOtherLayoutType = "DBD-VIEW-024";
    /// <summary>A layout places a widget the document does not define.</summary>
    public const string UnknownWidget = "DBD-VIEW-025";
    /// <summary>A tile outside its grid or canvas.</summary>
    public const string TileOutside = "DBD-VIEW-026";
    /// <summary>Two tiles that overlap (on a canvas: on the same layer).</summary>
    public const string TilesOverlap = "DBD-VIEW-027";
    /// <summary>A canvas tile below its widget type's minimum at scale 1.</summary>
    public const string TileBelowMinimum = "DBD-VIEW-028";
    /// <summary>A canvas whose size or scale limits are out of range or contradict each other.</summary>
    public const string BadCanvas = "DBD-VIEW-029";
    /// <summary>A flow layout with an unknown {token}, or a row that does not fit its twelve columns.</summary>
    public const string BadFlow = "DBD-VIEW-030";
    /// <summary>A layout omits the control of a required parameter that has no default.</summary>
    public const string ControlOmitted = "DBD-VIEW-031";
    /// <summary>A layout option that would change a widget (its type, encodings, query, formats or parameters).</summary>
    public const string OptionChangesWidget = "DBD-VIEW-032";
    /// <summary>A selector that cannot be read (a value outside its choices).</summary>
    public const string BadSelector = "DBD-VIEW-033";
    /// <summary>The default layout is of a type the kind does not allow.</summary>
    public const string DefaultLayoutType = "DBD-VIEW-034";
    /// <summary>A key of the other kind (<c>refresh</c> in a report, <c>strict</c> in a dashboard).</summary>
    public const string KeyOfOtherKind = "DBD-VIEW-035";
    /// <summary>A widget's <c>on_click</c> (or a custom widget's link list) names a link the document does not declare.</summary>
    public const string LinkNotDeclared = "DBD-VIEW-036";
    /// <summary>A drill or a link that does not give a required parameter of its target.</summary>
    public const string DrillMissingParameter = "DBD-VIEW-037";
    /// <summary>A link to a dashboard, report, view, page or form that the app (the <c>targets</c> a check is given) does not have: a target outside the app.</summary>
    public const string LinkTargetUnknown = "DBD-VIEW-039";
    /// <summary>A name in a link's <c>with</c>, <c>new</c>, <c>edit</c> or <c>lock</c> that the target has no parameter or field of.</summary>
    public const string LinkNameUnknown = "DBD-VIEW-044";
    /// <summary>A value whose type is not the type of the target's parameter or field.</summary>
    public const string LinkTypeMismatch = "DBD-VIEW-045";
    /// <summary><c>print: link</c> on a target that has no address of its own (a dialog or a panel): a PDF has nothing to point at.</summary>
    public const string LinkNoAddress = "DBD-VIEW-046";
    /// <summary>An <c>embed</c> widget in a report: a report is paged and exported, and a frame is neither (plan 32 DBR19: dashboards only).</summary>
    public const string EmbedInReport = "DBD-VIEW-038";
    /// <summary>A widget with no request to derive: a type without a query (text, image, control) or a query that is explicit (its encodings do not drive it).</summary>
    public const string NothingToDerive = "DBD-VIEW-040";
    /// <summary>A derived query that asks for no measure: a request needs at least one.</summary>
    public const string NoMeasure = "DBD-VIEW-041";
    /// <summary>Totals asked for twice (the matrix's encodings and the query), or on an axis that has no field.</summary>
    public const string TotalsConflict = "DBD-VIEW-042";
    /// <summary>The catalog check has no catalog for the model a widget's query names.</summary>
    public const string ModelNotBound = "DBD-VIEW-043";
    /// <summary>A widget names a field group the document does not declare.</summary>
    public const string UnknownGroup = "DBD-VIEW-047";
    /// <summary>A field that is in a group and also in another place of the same widget (an axis, a channel, another group): a field is in one place.</summary>
    public const string GroupConflict = "DBD-VIEW-048";
    /// <summary>An action's title, `show` or value names `ask.x` for a field the action does not ask for.</summary>
    public const string ActionAskUnknown = "DBD-VIEW-064";
    /// <summary>An action changes an entity, or runs a task, that the app's manifest does not allow (the `change` needs `write` or `change request` for the entity).</summary>
    public const string ActionNotAllowed = "DBD-VIEW-065";
    /// <summary>An action's <c>confirm: skip</c>, <c>stage</c> or <c>for: selection</c> where it cannot be: skip only for a field edit with no <c>ask</c>, stage only for a change, a selection only for an edit or a delete.</summary>
    public const string ActionModeInvalid = "DBD-VIEW-066";
    /// <summary>A model the document depends on is not pinned, has no such version, or does not load; or a model its widgets query is not a dependency (publishing, DBR10).</summary>
    public const string DependencyMissing = "DBD-VIEW-060";
    /// <summary>A saved query a widget runs is in none of the models the document depends on.</summary>
    public const string SavedQueryNotFound = "DBD-VIEW-061";
    /// <summary>A control lists the values of an attribute none of the models the document depends on has.</summary>
    public const string ControlAttributeUnknown = "DBD-VIEW-062";
    /// <summary>An image widget's hash is not among the files of the version being published.</summary>
    public const string ImageNotInDefinition = "DBD-VIEW-063";
    /// <summary>Warning: two layouts have equal selectors, so the later one is never reached.</summary>
    public const string EqualSelectors = "DBD-VIEW-080";
    /// <summary>Warning: a range with its minimum above its maximum matches nothing.</summary>
    public const string EmptyRange = "DBD-VIEW-081";
}
