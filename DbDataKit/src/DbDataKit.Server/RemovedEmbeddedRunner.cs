using DbDataKit.Config.Yaml;

namespace DbDataKit.Server;

/// <summary>
/// The console embeds no runner since plan 35 I2: its own state is kept by the shared housekeeping (plan 35 I1) and packages run on a paired DbDataRunner server. A clean break, never a silent one: a <c>settings.yaml</c> that still
/// has the <c>runner:</c> section is refused at start with a message naming the file and where each setting went.
/// </summary>
public static class RemovedEmbeddedRunner
{
    public const string Section = "runner";

    /// <summary>The sentence for a settings file that still has the section.</summary>
    public static string Sentence(string path) =>
        $"{path}: the console no longer embeds a runner (plan 35 I2), so the runner: section is not read. Its retention and backup settings are state.housekeeping (retention_every_minutes, backup_directory, backup_every_hours, backup_keep), " +
        "which the console runs in its own process; packages run on a DbDataRunner server paired with the console (dbdatakit-full installs one). Remove the runner: section to start the console.";

    /// <summary>Throws <see cref="InvalidOperationException"/> with <see cref="Sentence"/> when the console's settings file in <paramref name="directory"/> has a <c>runner:</c> section.</summary>
    public static void Refuse(string directory)
    {
        var path = ConsoleState.SettingsFileIn(directory);
        if (!File.Exists(path)) return;
        var root = StrictYamlReader.Read(File.ReadAllText(path), path, []);
        if (root is YamlMapping mapping && mapping.GetEntry(Section) is not null) throw new InvalidOperationException(Sentence(path));
    }
}
