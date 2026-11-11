namespace DbDataKit.Apps;

/// <summary>What a custom view is (plan 32 section 9f): a <see cref="Widget"/> a dashboard or report places in a box, or a <see cref="Page"/>, a whole dashboard or report written as code (it paginates as the top-level document).</summary>
public enum CustomViewKind { Widget, Page }

/// <summary>A parameter a custom view declares: what the document (or the address of a page) gives it. <see cref="Type"/> is one of <c>string</c>, <c>integer</c>, <c>number</c>, <c>boolean</c>, the core's own names.</summary>
public sealed record CustomViewParam(string Name, string Type, bool Many, bool Required, bool HasDefault, SpecPos At);

/// <summary>
/// A query a custom view declares, run by the host as the viewer (as the subscriber for a subscription) with its rows handed to the code, so the code never fetches data on the paths that print or email.
/// <see cref="Form"/> is <c>semantic</c> (a request against a semantic model the manifest lists) or <c>store</c> (an entity of a Store model the manifest lists with <c>read</c>); <see cref="Entity"/> is the store form's.
/// <see cref="Members"/> are the measures and attributes a semantic request names, for the check against the model's catalog.
/// </summary>
public sealed record CustomViewQuery(string Name, string Form, string Model, string? Entity, IReadOnlyList<string> Members, SpecPos At, SpecPos ModelAt);

/// <summary>
/// An entry of the manifest's <c>views</c> that is a mapping (it is additive: a plain file path is still a view-spec, a dashboard or a report). <see cref="Name"/> addresses it (a page at
/// <c>/apps/&lt;instance&gt;/_views/&lt;name&gt;</c>, the catalog lists it by it), <see cref="Export"/> is the name its code registers with <c>defineWidget</c> or <c>definePage</c> of <c>@dbdatakit/views-sdk</c>
/// (a dashboard's <c>component: &lt;app&gt;/&lt;Export&gt;</c> names it), <see cref="Formats"/> are the exports a page offers and <see cref="PageSize"/> its paper.
/// </summary>
public sealed record CustomView(string Name, CustomViewKind Kind, string Export, string? Title, IReadOnlyList<CustomViewParam> Params, IReadOnlyList<CustomViewQuery> Queries,
    IReadOnlyList<string> Formats, string? PageSize, string? Orientation, SpecPos At, SpecPos ExportAt);
