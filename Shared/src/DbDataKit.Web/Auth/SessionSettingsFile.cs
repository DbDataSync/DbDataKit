using System.Globalization;
using DbDataKit.Config.Yaml;
using DbDataKit.State;

namespace DbDataKit.Web.Auth;

/// <summary>
/// Reads the <c>session:</c> section of a host's settings file (plan 13a U22): the console's <c>settings.yaml</c> and the identity home's <c>server.yaml</c>.
/// <code>
/// session:
///   length_hours: 12      # from sign-in, 1 to 24 (default 12); use does not extend it
///   idle_minutes: 0       # a session unused for longer is refused; 0 is off (default)
/// </code>
/// A file without the section gives <see cref="SessionSettings.Default"/>. A setting that is not one, or a value out of range, is refused with a message that names the file and the setting.
/// </summary>
public static class SessionSettingsFile
{
    public const string Section = "session";

    public static SessionSettings Load(string path)
    {
        if (!File.Exists(path)) return SessionSettings.Default;
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(File.ReadAllText(path), path, diagnostics);
        // the file also holds what other parts read (the instance id and the paired clients, the state and the runner): only this section is judged here
        if (root is not YamlMapping mapping || mapping.GetEntry(Section) is not { } entry) return SessionSettings.Default;
        if (entry.Value is not YamlMapping section)
            return entry.Value is YamlScalar { Value.Length: 0 } ? SessionSettings.Default : throw new InvalidOperationException($"{path}: session is a set of settings (length_hours, idle_minutes).");

        var result = SessionSettings.Default;
        foreach (var item in section.Entries)
        {
            var key = item.Key.Value;
            var text = item.Value is YamlScalar s ? s.Value : throw new InvalidOperationException($"{path}: session.{key} is a single number.");
            switch (key)
            {
                case "length_hours":
                    result = result with { Length = TimeSpan.FromHours(Number(path, key, text, "hours from 1 to 24")) };
                    break;
                case "idle_minutes":
                    var minutes = Number(path, key, text, "minutes, 0 or more (0 turns the idle timeout off)");
                    result = result with { IdleTimeout = minutes == 0 ? null : TimeSpan.FromMinutes(minutes) };
                    break;
                default:
                    throw new InvalidOperationException($"{path}: '{key}' is not a session setting (length_hours, idle_minutes).");
            }
        }

        if (result.Problem(path) is { } problem) throw new InvalidOperationException(problem);
        return result;
    }

    private static double Number(string path, string key, string text, string meaning) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : throw new InvalidOperationException($"{path}: session.{key} is a number of {meaning}, not '{text}'.");
}
