using System.Text.Json.Nodes;
using DbDataGateway.Server.Configuration;
using DbDataKit.Views.Export;

namespace DbDataGateway.Server.Export;

/// <summary>What this host has of the export component, for <c>export status</c> and the console's card (plan 32 DBR16): installed or not, which release and browser, whether it is whole, and whether <c>views.export</c> points at it.</summary>
public static class ExportComponentStatus
{
    /// <summary>The component folder this host uses: the one <c>views.export.pin</c> names when it is an installed component, else the default folder of the data directory.</summary>
    public static string FolderOf(ViewsExportOptions export, string dataDirectory) =>
        !string.IsNullOrEmpty(export.Pin) && Path.GetDirectoryName(Path.GetFullPath(export.Pin)) is { } pinned && File.Exists(Path.Combine(pinned, ExportComponent.ManifestFile)) ? pinned : ExportInstaller.DefaultDirectory(dataDirectory);

    public static JsonObject Describe(ViewsExportOptions export, string dataDirectory)
    {
        var folder = FolderOf(export, dataDirectory);
        var rid = ExportComponent.CurrentRid();
        var manifest = ExportComponent.ReadManifest(folder);
        var problems = manifest is null || rid is null ? [] : ExportComponent.Verify(folder, rid);
        var expected = ExportComponent.SettingsFor(folder);
        var configured = string.Equals(export.Mode, "pinned", StringComparison.OrdinalIgnoreCase) && Same(export.Pin, expected["views.export.pin"]) && Same(export.PageAssets, expected["views.export.pageAssets"]);
        string summary;
        if (rid is null) summary = "No browser is pinned for this platform; set views.export to an installed browser (mode: system).";
        else if (manifest is null) summary = string.IsNullOrEmpty(export.Mode) ? "PDF export is not installed." : $"PDF export is set (mode {export.Mode}) and no component is installed in {folder}.";
        else if (problems.Count > 0) summary = $"The installed component is not whole: {problems[0]}";
        else if (!configured) summary = $"The component (release {ExportInstaller.ReadVersion(folder) ?? "unknown"}, Chromium {manifest.Chromium}) is installed in {folder}; views.export does not point at it.";
        else summary = $"PDF export is installed: release {ExportInstaller.ReadVersion(folder) ?? "unknown"}, Chromium {manifest.Chromium}.";
        return new JsonObject
        {
            ["platform"] = rid,
            ["supported"] = rid is not null,
            ["installed"] = manifest is not null,
            ["whole"] = manifest is not null && problems.Count == 0,
            ["directory"] = folder,
            ["release"] = manifest is null ? null : ExportInstaller.ReadVersion(folder),
            ["chromium"] = manifest?.Chromium,
            ["fonts"] = manifest?.Fonts,
            ["mode"] = string.IsNullOrEmpty(export.Mode) ? null : export.Mode,
            ["configured"] = configured,
            ["problems"] = new JsonArray([.. problems.Select(p => (JsonNode)p)]),
            ["summary"] = summary,
        };
    }

    private static bool Same(string? a, string b) => a is not null && string.Equals(Path.GetFullPath(a), b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
