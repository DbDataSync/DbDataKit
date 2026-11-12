using DbDataKit.Config.Yaml;
using DbDataKit.State.Tracking;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// The <c>tracking:</c> section of the Build server's <c>server.yaml</c> (plan 28c E16, plan 29 section 4a.2): where the copy of the tracking tables is kept. <c>mode</c> is <c>target</c> (the records stay on the target, nothing is copied), <c>state</c> or
/// <c>both</c>; <c>primary</c> is the side decisions are made from and is named for <c>both</c> only. The default is <c>both</c> with the target primary. Read strictly: a key that is not a setting stops the start naming the file and the nearest key that is.
/// <para>
/// The default keeps the target as the primary (owner, 2026-11-04) and copies to the state: <c>both</c> with <c>primary: target</c>; <c>target</c> keeps no copy. <c>primary: state</c> (or <c>mode: state</c>) makes the commands the server starts
/// write every record to the state first and to the target second (plan 28c E16, <see cref="TrackingJournals"/>): the server hands the primary to them (<see cref="PrimaryOverride"/>), so what the console's switch writes here is what the scheduled
/// refreshes follow. The section is read as the shared <see cref="TrackingSettings"/> (Sync's per-replication setting, plan 28c QE4, reuses it).
/// </para>
/// </summary>
internal static class BuildTrackingSettings
{
    public const string Section = "tracking";

    private static readonly string[] Keys = ["mode", "primary"];

    /// <summary>The setting of the server in <paramref name="serverDirectory"/>; a file without the section, or no file, is <see cref="TrackingSettings.Default"/>. Raises <see cref="InvalidOperationException"/> naming <c>server.yaml</c>.</summary>
    public static TrackingSettings Read(string serverDirectory)
    {
        var file = BuildState.SettingsFile(serverDirectory);
        if (!File.Exists(file)) return TrackingSettings.Default;
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(File.ReadAllText(file), file, diagnostics) as YamlMapping;
        if (root?.Get(Section) is not { } node) return TrackingSettings.Default;
        if (node is not YamlMapping map) throw new InvalidOperationException($"{file}: '{Section}' has to be a set of keys (mode, primary), not a single value or a list.");
        foreach (var entry in map.Entries)
        {
            if (Keys.Contains(entry.Key.Value, StringComparer.Ordinal)) continue;
            var near = DidYouMean.Closest(entry.Key.Value, Keys);
            throw new InvalidOperationException($"{file}: {Section}.{entry.Key.Value} is not a tracking setting (the settings are {string.Join(", ", Keys)}){(near is null ? "" : $"; did you mean '{near}'?")}");
        }

        string? Text(string key) => map.Get(key) is YamlScalar { Value: var v } ? v : null;
        var mode = Text("mode") ?? "both";
        var primary = Text("primary") ?? (mode.Equals("both", StringComparison.OrdinalIgnoreCase) ? "target" : null);
        try
        {
            return TrackingSettings.Parse(mode, primary);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"{file}: {Section}: {ex.Message}");
        }
        catch (InvalidOperationException ex) when (!ex.Message.StartsWith(file, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{file}: {Section}: {ex.Message}");
        }
    }

    /// <summary>True when the file has a <c>tracking:</c> section: the server then says its own primary, which wins over the projects' keys.</summary>
    public static bool IsExplicit(string serverDirectory)
    {
        var file = BuildState.SettingsFile(serverDirectory);
        if (!File.Exists(file)) return false;
        var root = StrictYamlReader.Read(File.ReadAllText(file), file, new List<ConfigDiagnostic>()) as YamlMapping;
        return root?.Get(Section) is not null;
    }

    /// <summary>What the server hands the commands it starts as <see cref="TrackingJournals.PrimaryVariable"/>: <c>state</c> or <c>target</c> when the file says its primary, null when it has no section (the projects' keys decide) or cannot be read (nothing is overridden).</summary>
    public static string? PrimaryOverride(string serverDirectory)
    {
        try
        {
            if (!IsExplicit(serverDirectory)) return null;
            var settings = Read(serverDirectory);
            return settings.Mode == TrackingMode.Target ? "target" : settings.PrimarySide == TrackingSide.State ? "state" : "target";
        }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>Writes the mode and primary into <c>server.yaml</c>, keeping every other line, comment and section (the file's editor splices), and reads the file back so what was written is a setting the server accepts.</summary>
    public static void Write(string serverDirectory, TrackingSettings settings)
    {
        if (settings.Mode != TrackingMode.Both) throw new ArgumentException("A switch of the primary is made in mode both.", nameof(settings));
        var file = BuildState.SettingsFile(serverDirectory);
        Directory.CreateDirectory(serverDirectory);
        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        string Apply(string current, string path, string value)
        {
            var result = YamlEditor.Set(current, path, new YamlValue(value), file);
            return result.Ok ? result.Text! : throw new InvalidOperationException(result.Problem!.ToString());
        }

        text = Apply(text, $"{Section}.mode", settings.Mode.ToString().ToLowerInvariant());
        text = Apply(text, $"{Section}.primary", settings.PrimarySide.ToString().ToLowerInvariant());
        var temporary = file + ".new";
        File.WriteAllText(temporary, text, new System.Text.UTF8Encoding(false));
        File.Move(temporary, file, overwrite: true);
        var back = Read(serverDirectory);
        if (back.Mode != settings.Mode || back.PrimarySide != settings.PrimarySide) throw new InvalidOperationException($"{file}: the tracking setting written did not read back.");
    }
}
