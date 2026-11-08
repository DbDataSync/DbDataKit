namespace DbDataKit.Views;

/// <summary>
/// The diagnostic codes of the dashboard and report loaders (series <c>DBD-VIEW-</c>). A key that is not known, a missing key and a value of the wrong shape use the shared codes
/// <c>DBD-104</c>, <c>DBD-105</c> and <c>DBD-106</c>, everything else has its own code so a test, an editor and an agent can tell the refusals apart.
/// The TypeScript validators of <c>@dbdatakit/visuals</c> report the same codes at the same paths.
/// </summary>
public static class ViewsCodes
{
    public const string UnknownKey = "DBD-104";
    public const string MissingKey = "DBD-105";
    public const string InvalidValue = "DBD-106";

    /// <summary>Text with raw HTML, an image, or a link that leaves the host.</summary>
    public const string UnsafeText = "DBD-VIEW-002";

    /// <summary>An image by URL, an SVG, or anything but a raster held by hash.</summary>
    public const string UnsafeImage = "DBD-VIEW-003";

    /// <summary>A chart spec outside the Vega-Lite subset: data, a transform, an aggregate, a bin, an expression.</summary>
    public const string ChartNotInSubset = "DBD-VIEW-004";

    /// <summary>An encoded field that is not an output of the widget's explicit query.</summary>
    public const string FieldNotInQuery = "DBD-VIEW-005";

    /// <summary>A widget type that is reserved (<c>embed</c>) or unknown.</summary>
    public const string ReservedWidget = "DBD-VIEW-006";

    /// <summary>A query that is not one of the forms, or a mode that does not fit it.</summary>
    public const string BadQuery = "DBD-VIEW-007";

    /// <summary>A parameter that is not declared (a marker, a control, a cross-filter, a drill).</summary>
    public const string UndeclaredParameter = "DBD-VIEW-008";

    /// <summary>A drill that does not give a required parameter of its target.</summary>
    public const string DrillMissingParameter = "DBD-VIEW-037";
}
