using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DbDataKit.Views.Export;

/// <summary>
/// The bundled font set (plan 32 section 4.6, DBR15b): Noto Sans, Noto Serif and Noto Sans Mono as WOFF2, installed by <c>scripts/fetch-fonts</c> under <c>.fonts/&lt;family&gt;/</c>. The print page declares them under the aliases
/// <c>DbDataKit Sans</c>, <c>DbDataKit Serif</c> and <c>DbDataKit Mono</c> and loads them from its own origin (nothing is installed into the operating system, so the result is the same on Windows and Linux).
/// Each file is checked against <c>fonts.pin</c> when the set is loaded: a changed byte refuses the set, naming the file.
/// </summary>
public sealed class BundledFonts
{
    private static readonly Regex FileName = new(@"^(?<family>noto-sans-mono|noto-sans|noto-serif)-(?<subset>[a-z]+(?:-ext)?)-(?<weight>400|700)-(?<style>normal|italic)\.woff2$", RegexOptions.CultureInvariant);

    /// <summary>The unicode-range of each Fontsource subset (the ranges Google Fonts publishes for them).</summary>
    private static readonly Dictionary<string, string> Ranges = new()
    {
        ["latin"] = "U+0000-00FF,U+0131,U+0152-0153,U+02BB-02BC,U+02C6,U+02DA,U+02DC,U+0304,U+0308,U+0329,U+2000-206F,U+20AC,U+2122,U+2191,U+2193,U+2212,U+2215,U+FEFF,U+FFFD",
        ["latin-ext"] = "U+0100-02BA,U+02BD-02C5,U+02C7-02CC,U+02CE-02D7,U+02DD-02FF,U+0304,U+0308,U+0329,U+1D00-1DBF,U+1E00-1E9F,U+1EF2-1EFF,U+2020,U+20A0-20AB,U+20AD-20C0,U+2113,U+2C60-2C7F,U+A720-A7FF",
        ["greek"] = "U+0370-0377,U+037A-037F,U+0384-038A,U+038C,U+038E-03A1,U+03A3-03FF",
        ["greek-ext"] = "U+1F00-1FFF",
        ["cyrillic"] = "U+0301,U+0400-045F,U+0490-0491,U+04B0-04B1,U+2116",
        ["cyrillic-ext"] = "U+0460-052F,U+1C80-1C8A,U+20B4,U+2DE0-2DFF,U+A640-A69F,U+FE2E-FE2F",
        ["vietnamese"] = "U+0102-0103,U+0110-0111,U+0128-0129,U+0168-0169,U+01A0-01A1,U+01AF-01B0,U+0300-0301,U+0303-0304,U+0308-0309,U+0323,U+0329,U+1EA0-1EF9,U+20AB",
    };

    /// <summary>The CSS family names the bundled set answers to (family folder to alias).</summary>
    public static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>
    {
        ["noto-sans"] = "DbDataKit Sans", ["noto-serif"] = "DbDataKit Serif", ["noto-sans-mono"] = "DbDataKit Mono",
    };

    private readonly List<(string Alias, string Url, string Subset, string Weight, string Style)> faces = [];
    private readonly Dictionary<string, PrintAsset> assets = new(StringComparer.Ordinal);

    private BundledFonts() { }

    /// <summary>The files, by the path the page loads them from (<c>fonts/noto-sans/noto-sans-latin-400-normal.woff2</c>).</summary>
    public IReadOnlyDictionary<string, PrintAsset> Assets => assets;

    public int FileCount => faces.Count;

    /// <summary>
    /// Reads the set from <paramref name="directory"/>. With <paramref name="pinFile"/> every file must be in the pin and match its SHA-256, and every pinned file must be there:
    /// a <see cref="ExportException"/> (<c>unavailable</c>) names the file otherwise.
    /// </summary>
    public static BundledFonts Load(string directory, string? pinFile)
    {
        if (!Directory.Exists(directory)) throw new ExportException("unavailable", $"the bundled fonts are not installed ({directory} does not exist); run scripts/fetch-fonts or set views.export.fonts.directory.");
        var pinned = pinFile is null ? null : ReadPin(pinFile);
        var set = new BundledFonts();
        foreach (var family in Aliases.Keys)
        {
            var folder = Path.Combine(directory, family);
            if (!Directory.Exists(folder)) throw new ExportException("unavailable", $"the bundled fonts lack the family folder {folder}; run scripts/fetch-fonts.");
            foreach (var file in Directory.EnumerateFiles(folder, "*.woff2").Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                var m = FileName.Match(name);
                if (!m.Success || m.Groups["family"].Value != family) continue;
                var bytes = File.ReadAllBytes(file);
                if (pinned is not null)
                {
                    if (!pinned.TryGetValue(name, out var sha)) throw new ExportException("unavailable", $"the bundled font {file} is not in the font pin.");
                    if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(sha, StringComparison.Ordinal)) throw new ExportException("unavailable", $"the bundled font {file} does not match the font pin (its SHA-256 differs); run scripts/fetch-fonts.");
                }
                var url = $"fonts/{family}/{name}";
                set.assets[url] = new PrintAsset(bytes, "font/woff2");
                set.faces.Add((Aliases[family], url, m.Groups["subset"].Value, m.Groups["weight"].Value, m.Groups["style"].Value));
            }
        }
        if (set.faces.Count == 0) throw new ExportException("unavailable", $"no bundled font files under {directory}; run scripts/fetch-fonts.");
        if (pinned is not null)
            foreach (var name in pinned.Keys)
                if (!set.assets.Keys.Any(k => k.EndsWith("/" + name, StringComparison.Ordinal))) throw new ExportException("unavailable", $"the font pin lists {name} and {directory} does not have it; run scripts/fetch-fonts.");
        return set;
    }

    /// <summary>The <c>@font-face</c> rules, the files taken from <paramref name="urlPrefix"/> (a relative path such as <c>""</c> or <c>"../"</c>).</summary>
    public string FontFaceCss(string urlPrefix = "")
    {
        var css = new StringBuilder();
        foreach (var f in faces)
            css.Append("@font-face{font-family:\"").Append(f.Alias).Append("\";font-style:").Append(f.Style).Append(";font-weight:").Append(f.Weight)
                .Append(";font-display:block;src:url(").Append(urlPrefix).Append(f.Url).Append(") format(\"woff2\");unicode-range:").Append(Ranges.GetValueOrDefault(f.Subset, Ranges["latin"])).Append("}\n");
        return css.ToString();
    }

    private static Dictionary<string, string> ReadPin(string pinFile)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(pinFile))
            if (line.StartsWith("FILE=", StringComparison.Ordinal) && line[5..].Split('|') is [_, var path, var sha])
                files[path[(path.LastIndexOf('/') + 1)..]] = sha.Trim();
        if (files.Count == 0) throw new ExportException("unavailable", $"{pinFile} has no FILE= lines: it is not a font pin.");
        return files;
    }
}
