using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.Views.Export;

/// <summary>What <c>export-component.json</c> says about a staged component (plan 32 DBR16, <c>dist/export_component.py stage</c>).</summary>
public sealed record ExportComponentManifest(string Rid, string Chromium, string Fonts, IReadOnlyDictionary<string, string> Print);

/// <summary>
/// The export component as installed (plan 32 DBR16): one folder holding <c>chromium.pin</c>, <c>.chromium/&lt;rid&gt;</c> (the pinned browser), <c>fonts.pin</c>, <c>.fonts</c> (each family with its OFL.txt) and
/// <c>print/</c> (the print page), with <c>export-component.json</c> naming what was staged. The release builds it (<c>dist/export_component.py</c>, a build-time tool); a host installs it from the release's zip
/// and points <c>views.export</c> at it. <see cref="Verify"/> is the same check as the tool's <c>verify</c>: the browser, every font and licence against the SHA-256 in the pins, the print files against the manifest;
/// a changed file is refused naming it. The folder is not the program folder: an upgrade replaces the program files and leaves it.
/// </summary>
public static class ExportComponent
{
    public const string ManifestFile = "export-component.json";
    public static readonly string[] PrintFiles = ["print.js", "print.css"];

    /// <summary>The platform name a component carries (<c>linux-x64</c>, <c>win-x64</c>), or null where no browser is pinned.</summary>
    public static string? CurrentRid() => ExportSettings.PinRid()?.Replace('_', '-');

    /// <summary>The settings that point a host at the component in <paramref name="folder"/>, by their path under <c>views.export</c> (<c>mode</c>, <c>pin</c>, <c>pageAssets</c>, <c>fonts.directory</c>, <c>fonts.pin</c>).</summary>
    public static IReadOnlyDictionary<string, string> SettingsFor(string folder)
    {
        var full = Path.GetFullPath(folder);
        return new Dictionary<string, string>
        {
            ["views.export.mode"] = "pinned",
            ["views.export.pin"] = Path.Combine(full, "chromium.pin"),
            ["views.export.pageAssets"] = Path.Combine(full, "print"),
            ["views.export.fonts.directory"] = Path.Combine(full, ".fonts"),
            ["views.export.fonts.pin"] = Path.Combine(full, "fonts.pin"),
        };
    }

    /// <summary>The manifest of an installed component, or null when the folder has none or it cannot be read.</summary>
    public static ExportComponentManifest? ReadManifest(string folder)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(Path.Combine(folder, ManifestFile))) is not JsonObject o) return null;
            var print = o["print"] is JsonObject p ? p.ToDictionary(e => e.Key, e => e.Value?.GetValue<string>() ?? "") : [];
            return new ExportComponentManifest(o["rid"]?.GetValue<string>() ?? "", o["chromium"]?.GetValue<string>() ?? "", o["fonts"]?.GetValue<string>() ?? "", print);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or FormatException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The problems of an installed component for <paramref name="rid"/>, each naming the file; empty when it is whole.</summary>
    public static IReadOnlyList<string> Verify(string folder, string rid)
    {
        var problems = new List<string>();
        var key = rid.Replace('-', '_');
        foreach (var name in new[] { "chromium.pin", "fonts.pin", ManifestFile })
            if (!File.Exists(Path.Combine(folder, name))) problems.Add($"{name} is missing from {folder}");
        if (problems.Count > 0) return problems;

        Dictionary<string, List<string>> chromium;
        try { chromium = ReadPin(Path.Combine(folder, "chromium.pin")); }
        catch (IOException ex) { return [$"chromium.pin cannot be read: {ex.Message}"]; }
        if (!chromium.TryGetValue("BIN_" + key, out var bin) || !chromium.TryGetValue("BIN_SHA256_" + key, out var binSha) || !chromium.TryGetValue("VERSION", out var version))
            return [$"chromium.pin cannot be used for {rid}: it lacks BIN_{key}, BIN_SHA256_{key} or VERSION"];
        var binary = Path.Combine(folder, ".chromium", rid, bin[0].Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(binary)) problems.Add($"the browser is missing: {binary}");
        else if (!Sha256(binary).Equals(binSha[0], StringComparison.OrdinalIgnoreCase)) problems.Add($"the browser {binary} does not match chromium.pin (its SHA-256 differs)");

        Dictionary<string, List<string>> fonts;
        try { fonts = ReadPin(Path.Combine(folder, "fonts.pin")); }
        catch (IOException ex) { problems.Add($"fonts.pin cannot be read: {ex.Message}"); return problems; }
        if (!fonts.TryGetValue("FAMILIES", out var families)) { problems.Add("fonts.pin has no FAMILIES"); return problems; }
        foreach (var family in families[0].Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var familyFolder = Path.Combine(folder, ".fonts", family);
            var licence = Path.Combine(familyFolder, "OFL.txt");
            if (!File.Exists(licence)) problems.Add($"the licence of {family} is missing: {licence} (the OFL requires it beside the files)");
            else if (!fonts.TryGetValue("LICENSE_SHA256_" + family.Replace('-', '_'), out var want) || !Sha256(licence).Equals(want[0], StringComparison.OrdinalIgnoreCase)) problems.Add($"the licence {licence} does not match fonts.pin");
            foreach (var entry in fonts.GetValueOrDefault("FILE") ?? [])
            {
                if (entry.Split('|') is not [var fam, var path, var digest] || fam != family) continue;
                var target = Path.Combine(familyFolder, path[(path.LastIndexOf('/') + 1)..]);
                if (!File.Exists(target)) problems.Add($"the font {target} is missing");
                else if (!Sha256(target).Equals(digest, StringComparison.OrdinalIgnoreCase)) problems.Add($"the font {target} does not match fonts.pin (its SHA-256 differs)");
            }
        }

        var manifest = ReadManifest(folder);
        if (manifest is null) { problems.Add($"{ManifestFile} cannot be read"); return problems; }
        foreach (var name in PrintFiles)
        {
            var target = Path.Combine(folder, "print", name);
            if (!File.Exists(target)) problems.Add($"the print page's {name} is missing: {target}");
            else if (!manifest.Print.TryGetValue(name, out var digest) || !Sha256(target).Equals(digest, StringComparison.OrdinalIgnoreCase)) problems.Add($"the print file {target} does not match {ManifestFile} (changed after staging)");
        }
        if (manifest.Chromium != version[0]) problems.Add($"{ManifestFile} names Chromium {manifest.Chromium} and chromium.pin names {version[0]}");
        if (manifest.Rid != rid) problems.Add($"{ManifestFile} is the component for {manifest.Rid}, this platform is {rid}");
        return problems;
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>KEY=value lines (comments and blanks skipped); a repeated key (<c>FILE=</c>) keeps every value.</summary>
    private static Dictionary<string, List<string>> ReadPin(string file)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || !line.Contains('=')) continue;
            var parts = line.Split('=', 2);
            if (!values.TryGetValue(parts[0], out var list)) values[parts[0]] = list = [];
            list.Add(parts[1]);
        }
        return values;
    }
}
