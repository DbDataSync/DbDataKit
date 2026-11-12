using System.Text;
using System.Text.Json.Nodes;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Export;
using DbDataKit.Cli;
using DbDataKit.Update;
using DbDataKit.Views.Export;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// <c>dbdatagateway export install|status</c> (plan 32 DBR16, owner decision 2026-11-12): the PDF export component (the pinned Chromium, the bundled fonts and the print page, about 420 MB installed) is a release asset of its own,
/// <c>dbdatagateway-export-v&lt;version&gt;-&lt;rid&gt;.zip</c> beside the product zips, and this command downloads the one for this platform, checks it against the release's <c>SHA256SUMS</c> and the pins inside it, puts it in the data
/// directory's <c>export-component</c> folder and sets <c>views.export</c> in <c>server.yaml</c> to use it. The console's "Install" button on the server settings screen runs the same <see cref="ExportInstaller"/>. A host with no
/// internet reads the release's files from a folder (<c>--source D:\release-files</c> or <c>DBDATAKIT_EXPORT_RELEASE_BASE</c>). The views module reads <c>views.export</c> when the gateway starts: restart it after an install.
/// </summary>
public static class ExportCommand
{
    private const string Where = "[--repo <data directory>]";

    public static Noun Noun() => new("export", "The PDF export component: install it from the release (the pinned Chromium, the bundled fonts, the print page) and show its state",
    [
        new Verb("install", "Download the export component of this release (or --version) for this platform, check it against the release's SHA256SUMS and its own pins, install it and set views.export to use it", EffectClass.RepoFilesOnly,
            $"[--version <release>] [--source <folder or https address of the release's files>] [--dir <install folder, default <data directory>/export-component>] [--rid linux-x64|win-x64] [--force] {Where}", InstallAsync,
            DataSchemas.Object([("directory", DataSchemas.String), ("release", DataSchemas.String), ("rid", DataSchemas.String), ("chromium", DataSchemas.String), ("fonts", DataSchemas.String), ("already_installed", DataSchemas.Boolean),
                ("downloaded_bytes", DataSchemas.Integer), ("settings_changed", DataSchemas.StringList), ("restart_needed", DataSchemas.Boolean)], "directory", "release", "rid", "chromium", "fonts", "already_installed", "downloaded_bytes", "settings_changed", "restart_needed")),
        new Verb("status", "Is the export component installed here, whole, and does views.export point at it", EffectClass.OfflineOnly, Where, StatusAsync,
            DataSchemas.Object([("installed", DataSchemas.Boolean), ("whole", DataSchemas.Boolean), ("configured", DataSchemas.Boolean), ("directory", DataSchemas.String), ("release", DataSchemas.String), ("chromium", DataSchemas.String), ("platform", DataSchemas.String),
                ("mode", DataSchemas.String), ("summary", DataSchemas.String), ("problems", DataSchemas.StringList)], "installed", "whole", "configured", "directory", "summary", "problems")),
    ]);

    private static async Task<int> InstallAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { "force" }, new HashSet<string> { "version", "source", "dir", "rid", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Usage: export install [--version <release>] [--source <folder or https address>] [--dir <folder>] [--rid linux-x64|win-x64] [--force] {Where}");
            return CliExit.Usage;
        }
        var repo = parsed.Get("repo") is { Length: > 0 } r ? Path.GetFullPath(r) : null;
        string dataDirectory;
        try { dataDirectory = repo ?? GatewayConfigFile.ResolveDataDirectory(); }
        catch (GatewayConfigException ex) { await context.Error.WriteLineAsync(ex.Message); return CliExit.Failure; }
        var installer = new ExportInstaller(ExportInstaller.DefaultFetcher(), dataDirectory, GatewayHost.Version);
        var progress = new ExportProgressSink(p => context.Out.WriteLine(p.Step + (p.Total > 0 ? $" ({p.Bytes / (1024 * 1024)} of {p.Total / (1024 * 1024)} MB)" : "")));
        ExportInstallResult result;
        try
        {
            result = await installer.InstallAsync(new ExportInstallRequest(parsed.Get("version"), parsed.Get("source"), parsed.Get("dir") is { Length: > 0 } d ? Path.GetFullPath(d) : null, parsed.Get("rid"), parsed.Has("force")), progress);
        }
        catch (ExportInstallException ex) { await context.Error.WriteLineAsync(ex.Message); return CliExit.Failure; }

        IReadOnlyList<string> changed;
        try { changed = SetViewsExport(GatewayConfigFile.PathIn(dataDirectory), result.Directory); }
        catch (Exception ex) when (ex is ExportInstallException or IOException or UnauthorizedAccessException)
        {
            await context.Error.WriteLineAsync($"The component is installed in {result.Directory}, but views.export could not be set: {ex.Message}");
            return CliExit.Failure;
        }
        await context.Out.WriteLineAsync(result.AlreadyInstalled
            ? $"The export component of release {result.Version} is already installed in {result.Directory} and whole."
            : $"Installed the export component of release {result.Version} (Chromium {result.Chromium}) in {result.Directory} ({result.DownloadedBytes / (1024 * 1024)} MB downloaded).");
        await context.Out.WriteLineAsync(changed.Count == 0 ? "views.export already points at it." : $"Set in server.yaml: {string.Join(", ", changed)}.");
        await context.Out.WriteLineAsync("Restart the gateway to use it (the views module reads views.export when it starts). Behind a container's sandbox limits, also set views.export.noSandbox: true.");
        context.Data["directory"] = result.Directory;
        context.Data["release"] = result.Version;
        context.Data["rid"] = result.Rid;
        context.Data["chromium"] = result.Chromium;
        context.Data["fonts"] = result.Fonts;
        context.Data["already_installed"] = result.AlreadyInstalled;
        context.Data["downloaded_bytes"] = result.DownloadedBytes;
        context.Data["settings_changed"] = changed.ToList();
        context.Data["restart_needed"] = true;
        return CliExit.Ok;
    }

    /// <summary>Points <c>views.export</c> of the file at the component, as the console's route does (that one also commits it); the file is replaced atomically.</summary>
    public static IReadOnlyList<string> SetViewsExport(string serverYaml, string componentDirectory)
    {
        var text = File.Exists(serverYaml) ? File.ReadAllText(serverYaml) : "";
        var (edited, changed) = ExportSettingsEdit.Apply(text, ExportComponent.SettingsFor(componentDirectory), serverYaml);
        if (changed.Count == 0) return changed;
        Directory.CreateDirectory(Path.GetDirectoryName(serverYaml)!);
        var temp = serverYaml + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temp, edited, new UTF8Encoding(false));
        File.Move(temp, serverYaml, overwrite: true);
        return changed;
    }

    private static async Task<int> StatusAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Usage: export status {Where}");
            return CliExit.Usage;
        }
        var repo = parsed.Get("repo") is { Length: > 0 } r ? Path.GetFullPath(r) : null;
        GatewayOptions options;
        string dataDirectory;
        try
        {
            options = GatewayConfiguration.LoadOptions(repo);
            dataDirectory = repo ?? GatewayConfigFile.ResolveDataDirectory();
        }
        catch (GatewayConfigException ex) { await context.Error.WriteLineAsync(ex.Message); return CliExit.Failure; }
        var status = ExportComponentStatus.Describe(options.Views.Export, dataDirectory);
        await context.Out.WriteLineAsync((string?)status["summary"] ?? "");
        foreach (var problem in (JsonArray)status["problems"]!) await context.Out.WriteLineAsync("  " + (string?)problem);
        context.Data["installed"] = (bool?)status["installed"] ?? false;
        context.Data["whole"] = (bool?)status["whole"] ?? false;
        context.Data["configured"] = (bool?)status["configured"] ?? false;
        context.Data["directory"] = (string?)status["directory"] ?? "";
        if ((string?)status["release"] is { } release) context.Data["release"] = release;
        if ((string?)status["chromium"] is { } chromium) context.Data["chromium"] = chromium;
        if ((string?)status["platform"] is { } platform) context.Data["platform"] = platform;
        if ((string?)status["mode"] is { } mode) context.Data["mode"] = mode;
        context.Data["summary"] = (string?)status["summary"] ?? "";
        context.Data["problems"] = ((JsonArray)status["problems"]!).Select(p => (string?)p ?? "").ToList();
        return (bool?)status["installed"] == true && (bool?)status["whole"] != true ? CliExit.Failure : CliExit.Ok;
    }
}
