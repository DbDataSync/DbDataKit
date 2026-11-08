using DbDataBuild.Core;
using DbDataKit.Quality;
using Config = DbDataKit.Config.Yaml;
using Yaml = DbDataBuild.Models.Yaml;

namespace DbDataBuild.Models;

/// <summary>
/// One check of a model's file, read through the suite's check model (<c>DbDataKit.Quality</c>, plan 23a FQ01). <see cref="Check"/> is about the model (its dataset is the model's name) and its columns;
/// <see cref="InColumn"/> is the column whose declaration holds it, null for a check written in the model's own <c>checks:</c>. <see cref="Line"/> is where it starts in the file.
/// </summary>
public sealed record ModelCheck(CheckDefinition Check, string? InColumn, int Line);

/// <summary>
/// The format of a model file and its <c>checks</c> (plan 23a FQ02). A model file is written in the lowest format that can say what it says: format 1 has no <c>schemaVersion</c>; format 2 (<c>schemaVersion: 2</c>) may
/// carry <c>checks:</c> in the model and in a column's declaration. A format-1 file that has the section is refused naming the file and the line (DDB-623); an older tool refuses the whole format-2 file by its unknown key
/// (DDB-104), so no tool builds a model without the checks it was written with. Checks are read and validated here, never evaluated.
/// </summary>
public static class ModelChecksReader
{
    /// <summary>The key of the format number.</summary>
    public const string VersionKey = "schemaVersion";

    /// <summary>Reads <c>schemaVersion</c>: 1 when absent. Null (with a diagnostic) when it is not a positive whole number or is newer than this tool reads.</summary>
    public static int? ReadVersion(Yaml.YamlMapping top, string file, List<Diagnostic> diags)
    {
        if (top.Get(VersionKey) is not { } node) return 1;
        if (node is not Yaml.YamlScalar { Value: var text } || !int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.ModelFormatMismatch, new(file, node.Line, node.Column), "`schemaVersion` must be a positive whole number: 1, or 2 for a file that has `checks`.", Fix: "Write `schemaVersion: 2`."));
            return null;
        }
        if (version > ModelDefinitionLoader.Version)
        {
            diags.Add(new Diagnostic(DiagnosticCatalog.ModelFormatMismatch, new(file, node.Line, node.Column),
                $"`schemaVersion` is {version}, but this tool reads model files up to {ModelDefinitionLoader.Version} (format {ModelDefinitionLoader.Version - 1} is read as well); `{file}` was written by a newer version.", Fix: "Upgrade the tool."));
            return null;
        }
        return version;
    }

    /// <summary>The nodes that hold a <c>checks:</c> section: the model's own and each column's.</summary>
    private static IEnumerable<(Yaml.YamlNode Section, string? Column, Yaml.YamlNode Key)> Sections(Yaml.YamlMapping top)
    {
        if (top.Entries.FirstOrDefault(e => e.Key.Value == CheckSection.Key) is { } own) yield return (own.Value, null, own.Key);
        if (top.Get("columns") is Yaml.YamlSequence list)
            foreach (var item in list.Items.OfType<Yaml.YamlMapping>())
                if (item.Entries.FirstOrDefault(e => e.Key.Value == CheckSection.Key) is { } entry && (item.Get("name") as Yaml.YamlScalar)?.Value is { } column)
                    yield return (entry.Value, column, entry.Key);
    }

    /// <summary>
    /// The checks of the file: the model's own and the columns', in the order written. <paramref name="version"/> is what <see cref="ReadVersion"/> found; a section in a format-1 file is refused. <paramref name="columns"/> are the
    /// declared columns (empty for a copy, whose columns are the origin's: they are not compared).
    /// </summary>
    public static IReadOnlyList<ModelCheck> Read(Yaml.YamlMapping top, string modelName, int version, IReadOnlyList<ColumnDefinition> columns, string file, List<Diagnostic> diags)
    {
        var result = new List<ModelCheck>();
        var sections = Sections(top).OrderBy(s => s.Key.Line).ToList();   // in the order written
        if (sections.Count == 0) return result;

        if (version < 2)
        {
            foreach (var (_, _, key) in sections)
                diags.Add(new Diagnostic(DiagnosticCatalog.ModelFormatMismatch, new(file, key.Line, key.Column),
                    $"`checks` is a section of format 2 of the model file, and `{file}` does not say `schemaVersion: 2`.",
                    Fix: "Add `schemaVersion: 2` as the first key of the file. A tool that reads only the first format then refuses the file by its unknown key, which keeps it from building the model without its checks."));
            return result;
        }

        var declared = columns.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (section, column, _) in sections)
        {
            foreach (var read in ReadSection(section, column, modelName, declared, file, diags))
            {
                if (!ids.Add(read.Check.Id))
                    diags.Add(new Diagnostic(DiagnosticCatalog.ModelChecksInvalid, new(file, read.IdLine, read.IdColumn), $"The check id `{read.Check.Id}` is used twice in this model.", Fix: "Give each check its own id: results and blocks are kept by it."));
                else result.Add(new ModelCheck(read.Check, column, read.Line));
            }
        }
        return result;
    }

    private sealed record ReadCheck(CheckDefinition Check, int Line, int IdLine, int IdColumn);

    private static IEnumerable<ReadCheck> ReadSection(Yaml.YamlNode section, string? column, string modelName, HashSet<string> declared, string file, List<Diagnostic> diags)
    {
        void Bad(Yaml.YamlNode at, string found, string? fix = null) => diags.Add(new Diagnostic(DiagnosticCatalog.ModelChecksInvalid, new(file, at.Line, at.Column), found, Fix: fix));

        // the keys that say what a column's own check is about are refused: the declaration says it
        if (column is not null && section is Yaml.YamlSequence list)
        {
            var refused = false;
            foreach (var item in list.Items.OfType<Yaml.YamlMapping>())
                foreach (var entry in item.Entries.Where(e => e.Key.Value is "dataset" or "column" or "columns"))
                {
                    Bad(entry.Key, $"A check in the declaration of column `{column}` is about that column: `{entry.Key.Value}` is not written there.", $"Remove `{entry.Key.Value}:`, or move the check to the model's own `checks:`.");
                    refused = true;
                }
            if (refused) yield break;
        }

        var found = new List<Config.ConfigDiagnostic>();
        var checks = CheckSection.Read(Convert(section), file, found, modelName, defaultColumn: column);
        foreach (var d in found.Where(d => d.Severity == Config.ConfigSeverity.Error))
            diags.Add(new Diagnostic(DiagnosticCatalog.ModelChecksInvalid, new(file, d.Line, d.Column), $"{d.Message} ({d.Code})", Fix: d.Fix));
        if (checks is null || section is not Yaml.YamlSequence items) yield break;

        // the read checks are the entries, in order (a bad or repeated entry makes the whole section null above)
        for (var i = 0; i < checks.Count && i < items.Items.Count; i++)
        {
            var check = checks[i];
            if (items.Items[i] is not Yaml.YamlMapping entry) continue;
            var ok = true;
            if (column is not null && check.Kind == CheckKind.RowCountChange)
            {
                Bad(entry, $"`row_count_change` is about the whole model, not about column `{column}`.", "Write it in the model's own `checks:`.");
                ok = false;
            }
            if (entry.Get("dataset") is { } dataset && check.Attachment.Dataset != modelName)
            {
                Bad(dataset, $"A check in a model's file is about that model (`{modelName}`); this one is about `{check.Attachment.Dataset}`.", "Remove `dataset:`, or write the check in the file of the model it is about.");
                ok = false;
            }
            if (declared.Count > 0)
                foreach (var (name, at) in ColumnNodes(entry))
                    if (!declared.Contains(name))
                    {
                        diags.Add(new Diagnostic(DiagnosticCatalog.UnknownColumnReference, new(file, at.Line, at.Column), $"Check `{check.Id}` is about `{name}`, which is not declared in `columns`.", "A check names only columns the model declares in `columns`."));
                        ok = false;
                    }
            var id = entry.Get("id") as Yaml.YamlScalar;
            if (ok) yield return new ReadCheck(check, entry.Line, id?.Line ?? entry.Line, id?.Column ?? entry.Column);
        }
    }

    /// <summary>The columns a check names in its own entry (<c>column</c> or <c>columns</c>) with the node of each; the columns of a <c>references</c> belong to another dataset.</summary>
    private static IEnumerable<(string Name, Yaml.YamlNode At)> ColumnNodes(Yaml.YamlMapping entry)
    {
        if (entry.Get("column") is Yaml.YamlScalar one) yield return (one.Value, one);
        else if (entry.Get("columns") is Yaml.YamlSequence many)
            foreach (var item in many.Items.OfType<Yaml.YamlScalar>()) yield return (item.Value, item);
    }

    /// <summary>The column references of the model's own checks, for an edit that renames a column.</summary>
    public static IEnumerable<Yaml.YamlScalar> ColumnReferences(Yaml.YamlMapping top)
    {
        if (top.Get(CheckSection.Key) is not Yaml.YamlSequence list) yield break;
        foreach (var entry in list.Items.OfType<Yaml.YamlMapping>())
        {
            if (entry.Get("column") is Yaml.YamlScalar one) yield return one;
            else if (entry.Get("columns") is Yaml.YamlSequence many)
                foreach (var item in many.Items.OfType<Yaml.YamlScalar>()) yield return item;
        }
    }

    /// <summary>This tool's strict reader and the shared one read the same subset; the check model reads the shared nodes, so each is copied with its position.</summary>
    private static Config.YamlNode Convert(Yaml.YamlNode node) => node switch
    {
        Yaml.YamlScalar s => new Config.YamlScalar(s.Value, s.Line, s.Column) { Quoted = s.Quoted, Start = s.Start, End = s.End, Flow = s.Flow },
        Yaml.YamlSequence q => new Config.YamlSequence(q.Items.Select(Convert).ToList(), q.Line, q.Column) { Start = q.Start, End = q.End, Flow = q.Flow },
        Yaml.YamlMapping m => new Config.YamlMapping(m.Entries.Select(e => new Config.YamlEntry((Config.YamlScalar)Convert(e.Key), Convert(e.Value))).ToList(), m.Line, m.Column) { Start = m.Start, End = m.End, Flow = m.Flow },
        _ => throw new InvalidOperationException("Unknown YAML node."),
    };
}
