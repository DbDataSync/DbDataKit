namespace DbDataKit.Views;

/// <summary>Where a link's value comes from: a literal written in the document, <c>$field</c> of the row that was clicked, or <c>$params.name</c> of the document.</summary>
public enum LinkValueKind { Literal, Field, Param }

/// <summary>One value a link gives its target. <see cref="Text"/> is the name for a field or a parameter (without the <c>$</c> and <c>params.</c>) and the text of a literal; <see cref="LiteralType"/> is the JSON type of a literal (<c>string</c>, <c>integer</c>, <c>number</c>, <c>boolean</c>), empty for the other kinds.</summary>
public sealed record LinkValue(LinkValueKind Kind, string Text, string LiteralType, int Line, int Column);

/// <summary>
/// A link of the shared vocabulary (<c>links</c> of a dashboard or a report, plan 32 DBR35): one target of <see cref="TargetKind"/> (<c>dashboard</c>, <c>report</c>, <c>view</c>, <c>page</c>, <c>form</c>) in the same app
/// instance, <see cref="Values"/> under <see cref="ValuesKey"/> (<c>with</c>, or <c>new</c> and <c>edit</c> for a form), <see cref="As"/> (<c>navigate</c>, <c>dialog</c>, <c>panel</c>: the shell's), what
/// <see cref="ThenKind"/> re-runs, the prefilled fields a form shows read-only (<see cref="Lock"/>) and what an export draws (<see cref="Print"/>, <see cref="Image"/>; null means "by the element's meaning", DBR37).
/// </summary>
public sealed record ViewLink(string Name, string TargetKind, string Target, string ValuesKey, IReadOnlyDictionary<string, LinkValue> Values, string As, string ThenKind, IReadOnlyList<string> ThenWidgets,
    IReadOnlyList<string> Lock, string? Print, string? Image, string? Label, int Line, int Column)
{
    /// <summary>Whether the target has an address of its own, so a PDF can hold a hyperlink to it: not where the shell shows it in a dialog or a side panel.</summary>
    public bool HasAddress => As == "navigate";

    /// <summary>The key of the target in the <c>targets</c> a check is given: <c>form:orders</c>.</summary>
    public string TargetKey => $"{TargetKind}:{Target}";
}
