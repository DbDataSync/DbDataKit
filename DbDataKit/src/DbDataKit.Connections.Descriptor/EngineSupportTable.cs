using System.Text;

namespace DbDataKit.Connections.Descriptor;

/// <summary>The support tables of the documentation, written from the <see cref="EngineRegistry"/> and nothing else.</summary>
public static class EngineSupportTable
{
    /// <summary>The text of <c>docs/engine-support.md</c>: the engines by role, then what each status says about each engine.</summary>
    public static string Markdown(EngineRegistry registry)
    {
        var engines = registry.Engines.Where(e => e.Roles.Count > 0).OrderBy(e => e.DisplayName, StringComparer.Ordinal).ToList();
        var sb = new StringBuilder();
        sb.Append("# Engine support\n\n");
        sb.Append("Generated from the engine registry (`DbDataKit/src/DbDataKit.Connections.Descriptor/engines/*.engine.yaml`, plan 14 R5) by `EngineSupportTableTests`; do not edit it by hand. ")
          .Append("Change the descriptor and run the test with `UPDATE_GOLDEN=1`.\n\n");
        sb.Append("**yes** built and run against a real engine, **unverified** built and never run against one, **planned** not built (the note names the slice or plan), **no** not supported.\n\n");

        sb.Append("## By role\n\n| Engine | ").Append(string.Join(" | ", EngineRoles.All.Select(EngineRoles.Title))).Append(" |\n");
        sb.Append("| --- |").Append(string.Concat(EngineRoles.All.Select(_ => " --- |"))).Append('\n');
        foreach (var e in engines)
            sb.Append("| ").Append(e.DisplayName).Append(" | ").Append(string.Join(" | ", EngineRoles.All.Select(r => Cell(e.Role(r).Status)))).Append(" |\n");

        sb.Append("\n## What each says\n");
        foreach (var e in engines)
        {
            sb.Append("\n### ").Append(e.DisplayName).Append("\n\n");
            sb.Append("`").Append(e.Id).Append("`");
            if (e.Aliases.Count > 0) sb.Append(" (also ").Append(string.Join(", ", e.Aliases.Select(a => $"`{a}`"))).Append(')');
            sb.Append("; dialect `").Append(e.Dialect).Append('`');
            if (e.Provider is not null) sb.Append("; provider `").Append(e.Provider).Append('`');
            if (e.CatalogReader is not null) sb.Append("; catalog reader `").Append(e.CatalogReader).Append('`');
            if (e.DefaultPort is { } port) sb.Append("; port ").Append(port);
            sb.Append("\n\n");
            foreach (var role in EngineRoles.All)
            {
                var spec = e.Role(role);
                if (spec.Status == EngineSupport.No && spec.Note is null) continue;
                sb.Append("- ").Append(EngineRoles.Title(role)).Append(": ").Append(Cell(spec.Status));
                if (spec.Note is not null) sb.Append(". ").Append(char.ToUpperInvariant(spec.Note[0])).Append(spec.Note.AsSpan(1));
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    private static string Cell(EngineSupport status) => status switch
    {
        EngineSupport.Supported => "yes",
        EngineSupport.Unverified => "unverified",
        EngineSupport.Planned => "planned",
        _ => "no",
    };
}
