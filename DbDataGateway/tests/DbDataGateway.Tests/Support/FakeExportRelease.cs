using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbDataKit.Views.Export;

namespace DbDataGateway.Tests.Support;

/// <summary>
/// A release folder with a tiny fake export component (plan 32 DBR16): the files <c>dist/export_component.py stage</c> puts together (the pins, a "browser" that is a few bytes, two font families with their licences, the print
/// page, the manifest), zipped as <c>dbdatagateway-export-v&lt;version&gt;-&lt;rid&gt;.zip</c> beside a <c>SHA256SUMS</c>. The real Chromium is never downloaded in a test.
/// </summary>
public sealed class FakeExportRelease : IDisposable
{
    public const string Version = "2026.11.12.1000";
    public const string Chromium = "1.2.3.4";

    private readonly string root = Path.Combine(Path.GetTempPath(), "fake-export-" + Guid.NewGuid().ToString("N")[..8]);

    public FakeExportRelease(string? rid = null, string version = Version, bool listInSums = true, Action<string>? tamperWithComponent = null, Action<string>? tamperWithZip = null, bool changeArchiveAfterChecksums = false, bool testedPins = false)
    {
        Rid = rid ?? ExportComponent.CurrentRid() ?? "linux-x64";
        Folder = Path.Combine(root, "release");
        Directory.CreateDirectory(Folder);
        var component = Path.Combine(root, "component");
        Stage(component, Rid, testedPins);
        tamperWithComponent?.Invoke(component);
        var zip = Path.Combine(Folder, $"dbdatagateway-export-v{version}-{Rid}.zip");
        ZipFile.CreateFromDirectory(component, zip, CompressionLevel.Fastest, false);
        tamperWithZip?.Invoke(zip);
        // the checksums are those of the zip as the release built it, so a zip changed after this is refused
        var sums = new StringBuilder();
        if (listInSums) sums.Append(Sha(File.ReadAllBytes(zip))).Append("  ").Append(Path.GetFileName(zip)).Append('\n');
        // a release whose archive was changed after the checksums were made (a corrupt or tampered download)
        if (changeArchiveAfterChecksums) File.AppendAllText(zip, "x");
        sums.Append(new string('a', 64)).Append("  dbdatagateway-v").Append(version).Append("-").Append(Rid).Append(".zip\n");
        File.WriteAllText(Path.Combine(Folder, "SHA256SUMS"), sums.ToString());
    }

    public string Rid { get; }

    /// <summary>The folder holding the release's files.</summary>
    public string Folder { get; }

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void Stage(string folder, string rid, bool testedPins)
    {
        // the pins this program embeds (a component of the tested browser and fonts) or fixture values that differ from them
        var chromium = testedPins ? ExportPins.Chromium : Chromium;
        var fontsVersion = testedPins ? ExportPins.Fonts : "9.9.9";
        var families = testedPins ? ExportPins.Families.ToArray() : new[] { "noto-sans", "noto-serif" };
        var key = rid.Replace('-', '_');
        var browser = Encoding.UTF8.GetBytes("#!/bin/sh\necho browser\n");
        var bin = rid == "win-x64" ? "chrome-win64/chrome.exe" : "chrome-linux64/chrome";
        Write(folder, $".chromium/{rid}/{bin}", browser);
        Write(folder, "chromium.pin", Encoding.UTF8.GetBytes($"# fixture\nVERSION={chromium}\nBASE=https://example.test\nBIN_{key}={bin}\nBIN_SHA256_{key}={Sha(browser)}\n"));
        var pin = new StringBuilder($"# fixture\nVERSION={fontsVersion}\nFAMILIES={string.Join(' ', families)}\n");
        foreach (var family in families)
        {
            var font = Encoding.UTF8.GetBytes(family + "-regular");
            var licence = Encoding.UTF8.GetBytes("licence of " + family);
            Write(folder, $".fonts/{family}/{family}-latin-400-normal.woff2", font);
            Write(folder, $".fonts/{family}/OFL.txt", licence);
            pin.Append($"LICENSE_SHA256_{family.Replace('-', '_')}={Sha(licence)}\nFILE={family}|package/files/{family}-latin-400-normal.woff2|{Sha(font)}\n");
        }
        Write(folder, "fonts.pin", Encoding.UTF8.GetBytes(pin.ToString()));
        var js = Encoding.UTF8.GetBytes("window.__reportReady = true");
        var css = Encoding.UTF8.GetBytes("body{margin:0}");
        Write(folder, "print/print.js", js);
        Write(folder, "print/print.css", css);
        Write(folder, ExportComponent.ManifestFile, JsonSerializer.SerializeToUtf8Bytes(new { component = "export", rid, chromium, fonts = fontsVersion, print = new Dictionary<string, string> { ["print.js"] = Sha(js), ["print.css"] = Sha(css) } }));
    }

    private static void Write(string folder, string relative, byte[] bytes)
    {
        var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { /* a temporary folder */ }
    }
}
