namespace DbDataKit.Views;

/// <summary>
/// Where and how a document is shown, as far as selecting a layout cares (plan 32 section 3.2). <see cref="Width"/> and <see cref="Height"/> are the container's size in CSS pixels, null when there is
/// none (an export has no viewport); <see cref="Medium"/> is <c>screen</c>, <c>print</c> or <c>export</c>; <see cref="Layout"/> is a layout named by a URL (<c>?layout=print</c>), an export option or a subscription.
/// </summary>
public sealed record LayoutContext(double? Width = null, double? Height = null, string Medium = "screen", string? Layout = null);

/// <summary>Why a layout was chosen: it was named, a selector matched, or nothing matched and the default was taken.</summary>
public enum LayoutChoice { Named, Matched, Default }

/// <summary>The outcome of <see cref="LayoutSelector"/>: the layout and why, or a refusal in words (an unknown name).</summary>
public sealed record LayoutSelection(ViewLayout? Layout, LayoutChoice? Choice, string? Refusal)
{
    public bool Ok => Layout is not null;
}

/// <summary>
/// <c>selectLayout(doc, context)</c>: a pure function, the same in TypeScript (<c>@dbdatakit/visuals</c>) with the same goldens (<c>conformance/views/select-layout</c>).
/// (1) A named layout wins; an unknown name is refused naming the layouts the document has. (2) Otherwise the layouts with a <c>when</c> that matches are the candidates; a condition on a dimension the
/// context lacks does not match. (3) The most specific candidate wins (the most conditions), a tie goes to document order. (4) With no candidate, the default layout.
/// </summary>
public static class LayoutSelector
{
    public static LayoutSelection Select(ViewDocument document, LayoutContext context) => Select(document.Layouts, context);

    public static LayoutSelection Select(IReadOnlyList<ViewLayout> layouts, LayoutContext context)
    {
        if (context.Layout is { } name)
        {
            var named = layouts.FirstOrDefault(l => l.Name == name);
            return named is null
                ? new(null, null, $"The document has no layout named `{name}`; its layouts are {string.Join(", ", layouts.Select(l => $"`{l.Name}`"))}.")
                : new(named, LayoutChoice.Named, null);
        }
        ViewLayout? best = null;
        foreach (var layout in layouts)
            if (layout.When is { } when && Matches(when, context) && (best is null || when.Specificity > best.When!.Specificity)) best = layout;
        if (best is not null) return new(best, LayoutChoice.Matched, null);
        var def = layouts.FirstOrDefault(l => l.IsDefault);
        return def is null ? new(null, null, "The document has no default layout.") : new(def, LayoutChoice.Default, null);
    }

    /// <summary>Whether every condition of a selector holds in a context. An omitted condition matches anything; a condition on a dimension the context does not have does not match.</summary>
    public static bool Matches(LayoutWhen when, LayoutContext context)
    {
        if (when.Width is { } w && (context.Width is not { } cw || !w.Contains(cw))) return false;
        if (when.Height is { } h && (context.Height is not { } ch || !h.Contains(ch))) return false;
        if (when.Orientation is { } o)
        {
            if (context.Width is not { } ow || context.Height is not { } oh) return false;
            if ((oh > ow ? "portrait" : "landscape") != o) return false;
        }
        if (when.Aspect is { } a)
        {
            if (context.Width is not { } aw || context.Height is not { } ah || ah <= 0 || !a.Contains(aw / ah)) return false;
        }
        if (when.Medium is { } m && !m.Contains(context.Medium)) return false;
        return true;
    }
}
