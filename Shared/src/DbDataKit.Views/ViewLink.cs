namespace DbDataKit.Views;

/// <summary>Where a link's value comes from: a literal written in the document, <c>$field</c> of the row that was clicked, <c>$params.name</c> of the document, or (in an action) <c>$ask.name</c>, what the person types or picks in the popup.</summary>
public enum LinkValueKind { Literal, Field, Param, Ask }

/// <summary>One value a link gives its target. <see cref="Text"/> is the name for a field or a parameter (without the <c>$</c> and <c>params.</c>) and the text of a literal; <see cref="LiteralType"/> is the JSON type of a literal (<c>string</c>, <c>integer</c>, <c>number</c>, <c>boolean</c>), empty for the other kinds.</summary>
public sealed record LinkValue(LinkValueKind Kind, string Text, string LiteralType, int Line, int Column);

/// <summary>
/// A link of the shared vocabulary (<c>links</c> of a dashboard or a report, plan 32 DBR35): one target of <see cref="TargetKind"/> (<c>dashboard</c>, <c>report</c>, <c>view</c>, <c>page</c>, <c>form</c>) in the same app
/// instance, <see cref="Values"/> under <see cref="ValuesKey"/> (<c>with</c>, or <c>new</c> and <c>edit</c> for a form), <see cref="As"/> (<c>navigate</c>, <c>dialog</c>, <c>panel</c>: the shell's), what
/// <see cref="ThenKind"/> re-runs, the prefilled fields a form shows read-only (<see cref="Lock"/>) and what an export draws (<see cref="Print"/>, <see cref="Image"/>; null means "by the element's meaning", DBR37).
/// </summary>
public sealed record ViewLink(string Name, string TargetKind, string Target, string ValuesKey, IReadOnlyDictionary<string, LinkValue> Values, string As, string ThenKind, IReadOnlyList<string> ThenWidgets,
    IReadOnlyList<string> Lock, string? Print, string? Image, string? Label, int Line, int Column, ActionDefinition? Action = null)
{
    /// <summary>Whether the target has an address of its own, so a PDF can hold a hyperlink to it: not where the shell shows it in a dialog or a side panel.</summary>
    public bool HasAddress => As == "navigate";

    /// <summary>The key of the target in the <c>targets</c> a check is given: <c>form:orders</c>.</summary>
    public string TargetKey => $"{TargetKind}:{Target}";
}

/// <summary>One field the person fills in an action's popup (<c>ask</c>): a text, a number, an integer, a boolean or a date, or one of <see cref="Choices"/>.</summary>
public sealed record ActionAsk(string Name, string Label, string Type, IReadOnlyList<string> Choices, bool Required, string? Default);

/// <summary>What the confirmation popup says, in the declaration's words (never the widget's): a <see cref="Title"/> with <c>{{field}}</c> placeholders over the row, the fields it <see cref="Show"/>, and the <see cref="Button"/>'s text.</summary>
public sealed record ActionConfirm(string? Title, IReadOnlyList<string> Show, string? Button, bool Skip = false);

/// <summary>
/// The <c>change</c> or <c>task</c> of an action link (plan 32 section 9h part 4, DBR53). A <c>change</c> is an <see cref="Operation"/> (<c>insert</c>, <c>edit</c>, <c>delete</c>) on <see cref="Entity"/> of the Store model
/// <see cref="Model"/>: <see cref="Key"/> names the row (one value for a one-field key under the name <c>key</c>, or the key's fields by name) with its <see cref="Version"/> (null: the version at the moment of the call), <see cref="Set"/>
/// the values written (an insert's values are in <see cref="Set"/> too). <see cref="For"/> is <c>row</c> or <c>selection</c> (each selected row, reviewed once); <see cref="Stage"/> says a click stages the change in the shell's pending set instead of opening a popup (plan 32 DBR53b); <c>Confirm.Skip</c> applies a field edit at once. A <c>task</c> (<see cref="Operation"/> <c>run</c>) runs <see cref="Task"/> of <see cref="Package"/> with <see cref="Set"/> as its inputs.
/// </summary>
public sealed record ActionDefinition(string Kind, string Operation, string? Model, string? Entity, string? Package, string? Task, IReadOnlyDictionary<string, LinkValue> Key, LinkValue? Version,
    IReadOnlyDictionary<string, LinkValue> Set, ActionConfirm Confirm, IReadOnlyDictionary<string, ActionAsk> Ask, string For = "row", bool Stage = false);
