using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace DbDataKit.Views.Export;

/// <summary>Which browser exports use (plan 32 section 4.4): the one <c>chromium.pin</c> names, or one the host already has.</summary>
public enum BrowserMode
{
    /// <summary>The pinned Chrome for Testing build, hash-checked at install. A browser of another version is refused.</summary>
    Pinned,

    /// <summary>An installed Chrome or Edge: allowed, not supported for page-break guarantees; its version is recorded in every result.</summary>
    System,
}

/// <summary>The settings section <c>views.export</c> is refused: the message names the source (the file) and the key.</summary>
public sealed class ExportSettingsException(string message) : Exception(message);

/// <summary>
/// <c>views.export</c> (plan 32 DBR15, DBR16): export is off unless a host turns it on, which is giving the section. Read strictly: an unknown key fails naming the source.
/// <code>
/// views:
///   export:
///     mode: pinned            # pinned (default) | system
///     path: /opt/chromium/chrome   # the executable; pinned mode finds .chromium/&lt;rid&gt; next to chromium.pin when it is left out
///     pin: /opt/dbdatakit/chromium.pin   # where the pinned version is read from; default: the nearest chromium.pin above the program
///     deadline_seconds: 60    # one export, browser start to PDF
///     max_pages: 500
///     max_concurrent: 2       # exports at once per host
///     no_sandbox: false       # only where a container forces it; logged
///     page_assets: /opt/dbdatakit/print   # the folder with print.js and print.css (the visuals' print page)
///     fonts:
///       directory: /opt/dbdatakit/fonts   # the bundled set (.fonts of fetch-fonts); default: .fonts next to fonts.pin
///       pin: /opt/dbdatakit/fonts.pin     # each file is checked against it at start
///       allowed: [Calibri]    # host fonts a document may name
///       required: []          # host fonts the host promises: a missing one stops the export, naming it
/// </code>
/// </summary>
public sealed record ExportSettings
{
    public BrowserMode Mode { get; init; } = BrowserMode.Pinned;

    public string? Path { get; init; }

    public string? Pin { get; init; }

    /// <summary>The version a <see cref="BrowserMode.Pinned"/> host insists on; null reads it from <see cref="Pin"/>.</summary>
    public string? PinnedVersion { get; init; }

    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(60);

    public int MaxPages { get; init; } = 500;

    public int MaxConcurrent { get; init; } = 2;

    public bool NoSandbox { get; init; }

    public string? PageAssets { get; init; }

    public string? FontsDirectory { get; init; }

    public string? FontsPin { get; init; }

    public IReadOnlyList<string> AllowedFonts { get; init; } = [];

    public IReadOnlyList<string> RequiredFonts { get; init; } = [];

    private static readonly string[] Keys = ["mode", "path", "pin", "pinned_version", "deadline_seconds", "max_pages", "max_concurrent", "no_sandbox", "page_assets", "fonts"];
    private static readonly string[] FontKeys = ["directory", "pin", "allowed", "required"];

    /// <summary>Reads the section; <paramref name="source"/> is the file it came from, named in every refusal.</summary>
    public static ExportSettings Read(JsonObject section, string source)
    {
        foreach (var (key, _) in section)
            if (!Keys.Contains(key)) throw new ExportSettingsException($"{source}: views.export has no key `{key}`. Keys: {string.Join(", ", Keys)}.");
        var mode = BrowserMode.Pinned;
        if (Text(section, "mode", source) is { } m)
            mode = m switch { "pinned" => BrowserMode.Pinned, "system" => BrowserMode.System, _ => throw new ExportSettingsException($"{source}: views.export.mode is `pinned` or `system`, not `{m}`.") };
        var fonts = section["fonts"] as JsonObject;
        if (section["fonts"] is not null and not JsonObject) throw new ExportSettingsException($"{source}: views.export.fonts must be a mapping.");
        if (fonts is not null)
            foreach (var (key, _) in fonts)
                if (!FontKeys.Contains(key)) throw new ExportSettingsException($"{source}: views.export.fonts has no key `{key}`. Keys: {string.Join(", ", FontKeys)}.");
        return new ExportSettings
        {
            Mode = mode,
            Path = Text(section, "path", source),
            Pin = Text(section, "pin", source),
            PinnedVersion = Text(section, "pinned_version", source),
            Deadline = TimeSpan.FromSeconds(Number(section, "deadline_seconds", source, 1, 3600) ?? 60),
            MaxPages = (int)(Number(section, "max_pages", source, 1, 5000) ?? 500),
            MaxConcurrent = (int)(Number(section, "max_concurrent", source, 1, 64) ?? 2),
            NoSandbox = section["no_sandbox"] switch { null => false, JsonValue v when v.TryGetValue<bool>(out var b) => b, _ => throw new ExportSettingsException($"{source}: views.export.no_sandbox is true or false.") },
            PageAssets = Text(section, "page_assets", source),
            FontsDirectory = fonts is null ? null : Text(fonts, "directory", source, "views.export.fonts."),
            FontsPin = fonts is null ? null : Text(fonts, "pin", source, "views.export.fonts."),
            AllowedFonts = fonts is null ? [] : Names(fonts, "allowed", source),
            RequiredFonts = fonts is null ? [] : Names(fonts, "required", source),
        };
    }

    private static string? Text(JsonObject o, string key, string source, string prefix = "views.export.") =>
        o[key] switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) && s.Length > 0 => s,
            _ => throw new ExportSettingsException($"{source}: {prefix}{key} must be a non-empty text."),
        };

    private static double? Number(JsonObject o, string key, string source, double min, double max)
    {
        if (o[key] is null) return null;
        if (o[key] is JsonValue v && v.TryGetValue<double>(out var n) && n >= min && n <= max && n == Math.Floor(n)) return n;
        throw new ExportSettingsException($"{source}: views.export.{key} is a whole number from {min} to {max}.");
    }

    private static List<string> Names(JsonObject o, string key, string source) =>
        o[key] switch
        {
            null => [],
            JsonArray a => [.. a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : throw new ExportSettingsException($"{source}: views.export.fonts.{key} is a list of family names."))],
            _ => throw new ExportSettingsException($"{source}: views.export.fonts.{key} is a list of family names."),
        };

    /// <summary>The .NET runtime identifier the pin names a browser under (<c>linux_x64</c>, <c>win_x64</c>), or null where no pinned build exists.</summary>
    public static string? PinRid() =>
        RuntimeInformation.ProcessArchitecture != Architecture.X64 ? null
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux_x64"
        : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win_x64" : null;
}

/// <summary>What <c>chromium.pin</c> says (KEY=value lines): the version, and where the browser of this platform is installed under <c>.chromium/&lt;rid&gt;/</c>.</summary>
public sealed record ChromiumPin(string Version, string? Executable)
{
    /// <summary>Reads a pin file; the executable is the one <c>fetch-chromium</c> installs next to it, when this platform has one.</summary>
    public static ChromiumPin Read(string file)
    {
        var values = File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('=')).Select(l => l.Split('=', 2)).ToDictionary(p => p[0].Trim(), p => p[1].Trim());
        if (!values.TryGetValue("VERSION", out var version)) throw new ExportSettingsException($"{file}: the pin has no VERSION line.");
        var rid = ExportSettings.PinRid();
        string? exe = null;
        if (rid is not null && values.TryGetValue("BIN_" + rid, out var bin))
            exe = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(file))!, ".chromium", rid.Replace('_', '-'), bin.Replace('/', System.IO.Path.DirectorySeparatorChar));
        return new ChromiumPin(version, exe);
    }

    /// <summary>The nearest <c>chromium.pin</c> in <paramref name="start"/> or above it, or null.</summary>
    public static string? Find(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            if (File.Exists(System.IO.Path.Combine(dir.FullName, "chromium.pin"))) return System.IO.Path.Combine(dir.FullName, "chromium.pin");
        return null;
    }
}
