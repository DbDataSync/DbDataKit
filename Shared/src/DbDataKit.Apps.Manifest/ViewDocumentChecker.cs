using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps;

/// <summary>A parameter or field a link or drill may give a view of the app (plan 32 DBR13): a name, the core's type name (<c>string</c>, <c>integer</c>, <c>number</c>, <c>boolean</c>, <c>date</c>, or <c>any</c> where the type is the model's to say), whether it is a list, and whether it must be given.</summary>
public sealed record ViewDocumentTarget(string Name, string Type, bool Many, bool Required, bool HasDefault);

/// <summary>What a dashboard or a report file says about itself: its kind (<c>dashboard</c> or <c>report</c>) and its parameters, which links to it may give.</summary>
public sealed record ViewDocumentInfo(string Kind, IReadOnlyList<ViewDocumentTarget> Parameters);

/// <summary>What a document is checked against: the app's manifest and the targets a link or drill may lead to, keyed <c>dashboard:name</c>, <c>report:name</c>, <c>view:name</c>, <c>page:name</c> or <c>form:name</c> (a name is the file's name without folder and extension).</summary>
public sealed record ViewDocumentContext(AppManifest Manifest, IReadOnlyDictionary<string, IReadOnlyList<ViewDocumentTarget>> Targets);

/// <summary>
/// The loader of <c>dashboard/1</c> and <c>report/1</c> files an app lists under <c>views</c> (plan 32 DBR13). The manifest library does not read them (that is <c>DbDataKit.Views</c>, which implements this):
/// <see cref="Describe"/> says what a file is and takes, <see cref="Check"/> loads it with its file, line and column and checks it against the app.
/// </summary>
public interface IViewDocumentChecker
{
    /// <summary>The kind and parameters of a document, or null when it does not read (<see cref="Check"/> says why).</summary>
    ViewDocumentInfo? Describe(string text, string file);

    /// <summary>Every problem of the document: its own (the loader's) and its use of the app (the models, the targets of its links and drills, a custom widget's component and parameters).</summary>
    IReadOnlyList<ConfigDiagnostic> Check(string text, string file, ViewDocumentContext context);
}
