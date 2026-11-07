using System.Globalization;
using System.Text;
using DbDataKit.Config.Yaml;

namespace DbDataKit.State;

/// <summary>
/// Writes the <c>state:</c> section of a product's settings file (plan 29b slice C02), the counterpart of <see cref="StateSettingsReader"/>: <c>state init</c>, <c>state move</c> and the console's screens change a
/// service's state settings through this one class, so the file is always valid and the verbs behave the same in every product.
/// <list type="bullet">
/// <item><b>Only the <c>state:</c> mapping is replaced.</b> It is found with <c>DbDataKit.Config.Yaml</c>'s strict reader, which gives every node's source offsets, and exactly the lines of that entry are
/// replaced (or the section is appended at the end of the file). Every other line, comment, blank line, key order, quote style and line ending of the file is kept byte for byte. The library's editor
/// (<c>YamlEditor</c>) only sets scalars, so the section is written as a text block; comments <i>inside</i> the old <c>state:</c> block, and a comment at the end of its last line, are replaced with it.</item>
/// <item><b>Validated first.</b> The new section is parsed back and read through <see cref="StateSettingsReader"/> before anything is written; a setting it refuses (a retention of zero days, a server engine
/// without a connection, an engine it does not know) is refused with the file's name and the setting's key, and the file is not touched. A file that does not parse is refused too, not overwritten.</item>
/// <item><b>Atomic.</b> The new content is written to a temporary file in the same directory, flushed, and renamed over the original, so a crash leaves either the old file or the new one. A failed rename
/// removes the temporary file and leaves the original as it was.</item>
/// <item><b>Secrets stay references.</b> The connection string is written exactly as given (<c>Password=secret:state-db</c>, <c>env:</c>); nothing here resolves a reference, and no message quotes the string.</item>
/// </list>
/// What the section cannot express is not written: <see cref="RetentionSettings.OrphanGrace"/> is not a setting in the file.
/// </summary>
/// <param name="replaceFile">Moves the finished temporary file (first argument) over the settings file (second); the default is an atomic rename. A seam for a test that makes the rename fail.</param>
public sealed class StateSettingsWriter(Action<string, string>? replaceFile = null)
{
    private const string Section = "state";

    private readonly Action<string, string> _replace = replaceFile ?? ((from, to) => File.Move(from, to, overwrite: true));

    /// <summary>Replaces the <c>state:</c> section of <paramref name="path"/> with <paramref name="settings"/>, adding it at the end when there is none, and creating the file when it does not exist.</summary>
    /// <exception cref="InvalidOperationException">The settings are invalid, or the existing file cannot be edited safely; the message names the file and, for a setting, its key. The file is unchanged.</exception>
    public void Write(string path, StateSettings settings)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(settings);

        var existing = File.Exists(path) ? File.ReadAllText(path) : "";
        var nl = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var block = Render(settings, nl);
        Validate(path, settings, block);

        var root = Parse(path, existing);
        string updated;
        if (root is null)
        {
            updated = existing.Length > 0 && !existing.EndsWith('\n') ? existing + nl + block : existing + block;
        }
        else if (root.GetEntry(Section) is { } entry)
        {
            var (start, end) = Span(path, existing, root, entry);
            updated = existing[..start] + block + existing[end..];
        }
        else
        {
            if (root.Flow) throw new InvalidOperationException($"{path}: the file's top level is written in braces, which is not edited here; nothing was changed. Write it as a block mapping, or add the state section by hand.");
            updated = existing.Length > 0 && !existing.EndsWith('\n') ? existing + nl + block : existing + block;
        }

        Parse(path, updated);
        Commit(path, updated);
    }

    /// <summary>Removes the <c>state:</c> section from <paramref name="path"/>, leaving the rest as it is. Returns false when there is no file or no such section (nothing is written).</summary>
    public bool Remove(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) return false;
        var existing = File.ReadAllText(path);
        var root = Parse(path, existing);
        if (root?.GetEntry(Section) is not { } entry) return false;
        var (start, end) = Span(path, existing, root, entry);
        var updated = existing[..start] + existing[end..];
        Parse(path, updated);
        Commit(path, updated);
        return true;
    }

    /// <summary>The settings in <paramref name="path"/>'s <c>state:</c> section, read strictly (the defaults when the file or the section is absent): what a product reads at start.</summary>
    public static StateSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path)) return new StateSettings();
        var root = Parse(path, File.ReadAllText(path));
        if (root?.GetEntry(Section) is not { } entry) return new StateSettings();
        try { return StateSettingsReader.Read(entry.Value is YamlMapping map ? ToDictionary(map) : null, Section); }
        catch (InvalidOperationException ex) { throw new InvalidOperationException($"{path}: {ex.Message}", ex); }
    }

    /// <summary>The section as it is written to the file: <c>state:</c> and its settings, ending in a line break.</summary>
    public static string Render(StateSettings settings, string newline = "\n")
    {
        ArgumentNullException.ThrowIfNull(settings);
        var lines = new List<string>();
        if (settings.Engine is { Length: > 0 } engine) lines.Add($"  engine: {YamlText.Scalar(engine)}");
        if (settings.ConnectionString is { Length: > 0 } connection) lines.Add($"  connection: {YamlText.Scalar(connection)}");

        var namespaces = settings.Namespaces;
        if (Flow(namespaces.Registry) is { } registry) lines.Add($"  registry: {registry}");
        var entries = new List<string>();
        if (Flow(namespaces.Default) is { } defaults) entries.Add($"    default: {defaults}");
        foreach (var (name, setting) in namespaces.Modules) entries.Add($"    {YamlText.Scalar(name)}: {Flow(setting) ?? "{}"}");
        if (entries.Count > 0) lines.AddRange(["  namespaces:", .. entries]);

        var retention = settings.Retention;
        var retentionLines = new List<string>();
        if (retention.Days is { } days) retentionLines.Add($"    days: {Days(days)}");
        if (retention.Modules.Count > 0) retentionLines.Add($"    modules: {{ {string.Join(", ", retention.Modules.Select(p => $"{YamlText.FlowScalar(p.Key)}: {Days(p.Value)}"))} }}");
        if (retention.Tables.Count > 0) retentionLines.Add($"    tables: {{ {string.Join(", ", retention.Tables.Select(p => $"{YamlText.FlowScalar(p.Key)}: {Days(p.Value)}"))} }}");
        if (retentionLines.Count > 0) lines.AddRange(["  retention:", .. retentionLines]);

        return lines.Count == 0 ? $"{Section}: {{}}{newline}" : $"{Section}:{newline}{string.Join(newline, lines)}{newline}";
    }

    private static string? Flow(NamespaceSetting setting)
    {
        var parts = new List<string>();
        if (setting.Schema is not null) parts.Add($"schema: {YamlText.FlowScalar(setting.Schema)}");
        if (setting.Prefix is not null) parts.Add($"prefix: {YamlText.FlowScalar(setting.Prefix)}");
        if (setting.File is not null) parts.Add($"file: {YamlText.FlowScalar(setting.File)}");
        return parts.Count == 0 ? null : $"{{ {string.Join(", ", parts)} }}";
    }

    private static string Days(double days) => double.IsPositiveInfinity(days) ? "forever" : days.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Parses the new section back and reads it the way a product will; a refusal names the file and the setting.</summary>
    private static void Validate(string path, StateSettings settings, string block)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(block, path, diagnostics) as YamlMapping;
        if (diagnostics.Any(d => d.Severity == ConfigSeverity.Error) || root?.Get(Section) is null)
            throw new InvalidOperationException($"{path}: the state settings cannot be written as YAML ({diagnostics.FirstOrDefault()?.Message ?? "nothing to write"}); nothing was changed.");
        try
        {
            try { _ = settings.EngineId; }
            catch (InvalidOperationException ex) { throw new InvalidOperationException($"state.engine: {ex.Message}", ex); }
            var read = StateSettingsReader.Read(ToDictionary((YamlMapping)root.Get(Section)!), Section);
            if (!read.IsSqlite && string.IsNullOrWhiteSpace(read.ConnectionString))
                throw new InvalidOperationException($"state.connection is empty, and the engine {read.Engine} needs one.");
            if (settings.Namespaces.Modules.ContainsKey("default"))
                throw new InvalidOperationException("state.namespaces.default is the setting for every module, so no module is called default.");
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException($"{path}: {ex.Message} Nothing was changed.", ex);
        }
    }

    private static YamlMapping? Parse(string path, string text)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(text, path, diagnostics);
        if (diagnostics.FirstOrDefault(d => d.Severity == ConfigSeverity.Error) is { } error)
            throw new InvalidOperationException($"{path}: the file is not valid YAML ({error.Message}, line {error.Line}); it was left as it is.");
        return root switch
        {
            null => null,
            YamlMapping mapping => mapping,
            _ => throw new InvalidOperationException($"{path}: the file's top level is not a set of keys, so it has no state section to edit; it was left as it is."),
        };
    }

    /// <summary>The characters of the whole lines the <c>state</c> entry takes: from its key's line to the end of the line its value ends on.</summary>
    private static (int Start, int End) Span(string path, string text, YamlMapping root, YamlEntry entry)
    {
        if (root.Flow) throw new InvalidOperationException($"{path}: the file's top level is written in braces, which is not edited here; nothing was changed.");
        var start = text.LastIndexOf('\n', Math.Max(0, entry.Key.Start - 1));
        start = entry.Key.Start == 0 || start < 0 ? 0 : start + 1;
        if (text[start..entry.Key.Start].Trim().Length > 0)
            throw new InvalidOperationException($"{path}: the state key is not at the start of its line (line {entry.Key.Line}), which is not edited here; nothing was changed.");
        var last = entry.Value is YamlScalar { Value.Length: 0, Quoted: false } ? entry.Key.End : entry.Value.End;
        var newline = text.IndexOf('\n', Math.Max(0, last - 1));
        return (start, newline < 0 ? text.Length : newline + 1);
    }

    private static Dictionary<string, object?> ToDictionary(YamlMapping mapping) => mapping.Entries.ToDictionary(e => e.Key.Value, e => Convert(e.Value), StringComparer.Ordinal);

    private static object? Convert(YamlNode node) => node switch
    {
        YamlScalar { Quoted: false, Value: "" or "null" or "~" } => null,
        YamlScalar scalar => scalar.Value,
        YamlMapping mapping => ToDictionary(mapping),
        YamlSequence sequence => sequence.Items.Select(Convert).ToList(),
        _ => null,
    };

    private void Commit(string path, string text)
    {
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(full) && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, File.GetUnixFileMode(full));
            _replace(temp, full);
        }
        finally
        {
            try { File.Delete(temp); }
            catch (IOException) { /* a leftover temporary file is untidy and harmless; the original is what matters */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
