using DbDataBuild.Core;
using DbDataBuild.Semantic;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild project show semantic` (effect: offline only; plan 14 R1, plan 19 J08). The semantic models of the project (`semantic/`) as they compile: the connections they read, what every attribute and measure is
/// bound to (connection, model, column), the generated physical layer (a table per model and connection, with the model's grain as its key), the relationships and the hierarchies. Nothing is connected to.
/// </summary>
internal static class SemanticShowCommand
{
    public static int Run(CommandSpec spec, string root, string[] names, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        var ctx = ProjectContext.Load(root);
        var unknown = names.FirstOrDefault(n => ctx.Semantic.Models.All(m => m.Name != n && m.File != n));
        if (unknown != null)
        {
            error.WriteLine($"No semantic model `{unknown}`. {(ctx.Semantic.Models.Count == 0 ? $"The project has no files in {SemanticProject.Folder}/." : "Semantic models: " + string.Join(", ", ctx.Semantic.Models.Select(m => m.Name)) + ".")}");
            return CliApp.ExitUsage;
        }
        var chosen = ctx.Semantic.Models.Where(m => names.Length == 0 || names.Contains(m.Name) || names.Contains(m.File)).ToList();

        var problems = ctx.Diagnostics.Where(d => d.Severity == Severity.Error && d.Location.File.StartsWith(SemanticProject.Folder + "/", StringComparison.Ordinal)).ToList();
        foreach (var d in problems) error.Diag(d);
        var broken = chosen.Where(m => m.Compilation == null).ToList();
        if (broken.Count > 0)
        {
            output.WriteLine($"{broken.Count} semantic file(s) do not compile ({string.Join(", ", broken.Select(m => m.File))}): fix them (`{ProductInfo.Cli} project compile` shows the findings).");
            return CliApp.ExitFindings;
        }

        var documents = chosen.Select(m => Document(ctx, m)).ToList();
        output.Payload("models", documents);
        if (chosen.Count == 0)
        {
            output.WriteLine($"No semantic models: the project has no files in {SemanticProject.Folder}/.");
            return CliApp.ExitOk;
        }
        foreach (var m in chosen)
        {
            var compiled = m.Compilation!;
            output.WriteLine();
            output.WriteLine($"{m.Name}  ({m.File})");
            output.WriteLine("  sources: " + string.Join(", ", compiled.Sources.Keys.Order(StringComparer.Ordinal).Select(s => $"{s} = {m.Connections[s]} ({ctx.Config.Connections[m.Connections[s]].Engine})")));
            output.WriteLine($"  {"member",-30} {"kind",-10} {"source",-12} bound to");
            foreach (var b in compiled.Bindings)
                output.WriteLine($"  {b.Member,-30} {b.Kind,-10} {b.Source,-12} {b.Model}.{b.Column}{(b.Aggregate != null ? $"  ({b.Aggregate})" : "")}");
            foreach (var r in compiled.Relationships)
                output.WriteLine($"  relationship {r.Name}: {r.FromModel}({string.Join(", ", r.FromColumns)}) -> {r.ToModel}({string.Join(", ", r.ToColumns)}){(r.Role != null ? $" as {r.Role}" : "")}");
            foreach (var (name, levels) in Hierarchies(m))
                output.WriteLine($"  hierarchy {name}: {string.Join(" > ", levels)}");
        }
        output.WriteLine();
        output.WriteLine($"{chosen.Count} semantic model(s), {chosen.Sum(m => m.Compilation!.Bindings.Count)} binding(s).");
        return CliApp.ExitOk;
    }

    private static IEnumerable<(string Name, List<string> Levels)> Hierarchies(SemanticModelFile m) =>
        m.Authored!.Json["hierarchies"] is System.Text.Json.Nodes.JsonObject h
            ? h.Select(kv => (kv.Key, kv.Value!["levels"]!.AsArray().Select(l => l!.GetValue<string>()).ToList()))
            : [];

    private static object Document(ProjectContext ctx, SemanticModelFile m)
    {
        var compiled = m.Compilation!;
        return new
        {
            name = m.Name,
            file = m.File,
            description = m.Authored!.Json["description"]?.GetValue<string>(),
            sources = compiled.Sources.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => new { source = s.Key, connection = m.Connections[s.Key], engine = ctx.Config.Connections[m.Connections[s.Key]].Engine }).ToList(),
            bindings = compiled.Bindings.Select(b => new { member = b.Member, kind = b.Kind, source = b.Source, connection = m.Connections[b.Source], model = b.Model, column = b.Column, aggregate = b.Aggregate }).ToList(),
            tables = compiled.Tables.Select(t => new { name = t.Name, model = t.Model, source = t.Source, connection = m.Connections[t.Source], physical = t.Physical.Name, primary_key = t.Physical.PrimaryKey, columns = t.Physical.Columns.Select(c => new { name = c.Name, type = c.DataType }).ToList() }).ToList(),
            relationships = compiled.Relationships.Select(r => new { name = r.Name, from = r.FromModel, to = r.ToModel, from_columns = r.FromColumns, to_columns = r.ToColumns, source = r.Source, role = r.Role }).ToList(),
            hierarchies = Hierarchies(m).Select(h => new { name = h.Name, levels = h.Levels }).ToList(),
        };
    }
}
