using DbDataKit.Config.Yaml;
using DbDataKit.State.Tracking;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// The <c>tracking:</c> section of the Build server's <c>server.yaml</c> (plan 28c E16, plan 29 section 4a.2): where the copy of the tracking tables is kept. <c>mode</c> is <c>target</c> (the records stay on the target, nothing is copied), <c>state</c> or
/// <c>both</c>; <c>primary</c> is the side decisions are made from and is named for <c>both</c> only. The default is <c>both</c> with the target primary. Read strictly: a key that is not a setting stops the start naming the file and the nearest key that is.
/// <para>
/// This release keeps the target as the primary (owner, 2026-11-04) and copies to the state: <c>both</c> with <c>primary: target</c>, and <c>target</c> (no copy). <c>state</c> and a state primary parse but are refused with a message, until Build can
/// write its records to the state first; the section is read as the shared <see cref="TrackingSettings"/> so the setting does not change shape then (and Sync's per-replication setting, plan 28c QE4, can reuse it).
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
            var settings = TrackingSettings.Parse(mode, primary);
            if (settings.PrimarySide == TrackingSide.State)
                throw new InvalidOperationException("this release keeps the records on the target first (the state holds a copy); a state primary needs Build to write its records to the state, which it does not do yet");
            return settings;
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
}
