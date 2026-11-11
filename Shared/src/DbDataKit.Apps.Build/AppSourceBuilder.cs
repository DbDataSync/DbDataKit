using System.Text;
using DbDataKit.Apps.Bundling;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps;

/// <summary>One line a build said: <paramref name="Error"/> lines are why it failed or what is wrong with the result, the others are notes.</summary>
public sealed record AppBuildLine(bool Error, string Text);

/// <summary>Which bundler builds, or why none can (Bun not found), and what to say about the choice.</summary>
public sealed record AppBundlerChoice(IAppBundler? Bundler, string? Problem = null, string? Announcement = null);

/// <summary>
/// What <see cref="AppSourceBuilder"/> builds and how: the folders, the entry, the package store with its allow-list and the isolation. <c>Entry</c> is the script the bundle starts from, relative to the source; null: the conventional places
/// are looked in (<see cref="AppSourceBuilder.EntryConventions"/>), and without one the files are copied as they are. <c>Manifest</c> is the manifest file; null: <c>app.yaml</c> in the source.
/// </summary>
public sealed record AppBuildOptions(string Source, string Output, string? Entry = null, string? Manifest = null, bool Minify = true, TimeSpan? Timeout = null, bool Offline = false,
    VendoredPackageStore? Store = null, IReadOnlyList<string>? Allowed = null, NetworkIsolation Isolation = NetworkIsolation.IfAvailable);

/// <summary>
/// What a build did. <c>Succeeded</c>: the folder holds a built app that passes the built-in checks. <c>Lines</c> is everything the build said, in order. <c>Manifest</c> is the app's manifest when it was read. <c>Files</c> counts the files in
/// the output (the manifest included) and <c>Copied</c> how many of those were the app's own files copied across; <c>Bundled</c> names the files the bundler wrote; <c>Bundler</c> holds the bundler's diagnostics when it ran and failed (each a
/// line: a file, a line, a reason); <c>Entry</c> is the script that was bundled, if any.
/// </summary>
public sealed record AppBuildOutcome(bool Succeeded, IReadOnlyList<AppBuildLine> Lines, AppManifest? Manifest, int Files, int Copied, IReadOnlyList<string> Bundled, IReadOnlyList<string> Bundler, string? Entry)
{
    public IEnumerable<string> Errors => Lines.Where(l => l.Error).Select(l => l.Text);
    public IEnumerable<string> Notes => Lines.Where(l => !l.Error).Select(l => l.Text);
}

/// <summary>
/// Builds an app's source tree into the folder <c>apps validate</c> and the apps host accept (plan 26 AP23, plan 22a GP32 to GP35): the manifest is read first (its diagnostics name the line), the entry is bundled with an
/// <see cref="IAppBundler"/> (from the package store when there is one), the files of the source that are not source are copied, the manifest is written as <c>app.yaml</c> and the result is validated. The Gateway's
/// <c>apps build</c> and the Workspace server's build both call this, so the two cannot differ.
/// </summary>
public static class AppSourceBuilder
{
    /// <summary>Where the entry of a source tree is looked for when none is named.</summary>
    public static readonly string[] EntryConventions = ["src/main.tsx", "src/main.ts", "src/main.jsx", "src/main.js", "src/index.tsx", "src/index.ts", "src/index.jsx", "src/index.js"];

    private static readonly string[] SourceExtensions = [".ts", ".tsx", ".mts", ".cts", ".jsx", ".map"];

    private static readonly string[] ToolingFiles = ["package.json", "tsconfig.json", "bun.lock", "bun.lockb", "package-lock.json", "yarn.lock", "pnpm-lock.yaml"];

    /// <summary>Builds <paramref name="options"/>' source into its output. <paramref name="chooseBundler"/> is called when there is something to bundle, with the entry: the caller finds Bun, or hands a bundler it was given.</summary>
    public static async Task<AppBuildOutcome> BuildAsync(AppBuildOptions options, Func<string, AppBundlerChoice> chooseBundler, CancellationToken ct = default)
    {
        var lines = new List<AppBuildLine>();
        void Say(string text) => lines.Add(new AppBuildLine(false, text));
        AppBuildOutcome Fail(string text, AppManifest? manifest = null, IReadOnlyList<string>? bundler = null, string? entry = null)
        {
            lines.Add(new AppBuildLine(true, text));
            return new AppBuildOutcome(false, lines, manifest, 0, 0, [], bundler ?? [], entry);
        }

        var source = Path.GetFullPath(options.Source);
        var output = Path.GetFullPath(options.Output);
        var manifestPath = options.Manifest is { Length: > 0 } m ? Path.GetFullPath(m) : Path.Combine(source, "app.yaml");
        if (!Directory.Exists(source)) return Fail($"{source} is not a folder.");
        if (!File.Exists(manifestPath)) return Fail($"{manifestPath} does not exist: an app's manifest is app.yaml in its source folder, or name it with --manifest.");
        if (output == source || IsUnder(source, output)) return Fail($"The build folder {output} is the source folder or holds it; a build replaces its folder, so name another one.");
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any() && !File.Exists(Path.Combine(output, "app.yaml")))
            return Fail($"{output} is not empty and holds no app.yaml, so it does not look like an earlier build. A build replaces its folder; name a new or an empty one.");

        // the manifest first: it is cheap, and its diagnostics name the line
        var manifestText = await File.ReadAllTextAsync(manifestPath, ct);
        var load = AppManifestLoader.Load(manifestText, Path.GetFileName(manifestPath));
        foreach (var d in load.Diagnostics) lines.Add(new AppBuildLine(d.Severity == ConfigSeverity.Error, d.ToString()));
        if (!load.Ok) return new AppBuildOutcome(false, lines, null, 0, 0, [], [], null);

        var entry = options.Entry is { Length: > 0 } named ? named.Replace('\\', '/') : EntryConventions.FirstOrDefault(c => File.Exists(Path.Combine(source, c.Replace('/', Path.DirectorySeparatorChar))));
        if (entry is null) Say("No script entry (src/main.ts, src/main.js...) and no --entry: the files are copied as they are, nothing is bundled.");

        // an app with a script and view specs draws the specs with the library's components (plan 22a GP34): the specs, read by the host's own loader, go into the bundle as `src/views.generated.json`
        IReadOnlyDictionary<string, byte[]>? generated = null;
        if (entry is not null && load.Manifest!.Views.Count > 0)
            generated = ViewsApp.Generated(load.Manifest, file => File.Exists(Path.Combine(source, file.Replace('/', Path.DirectorySeparatorChar))) ? File.ReadAllBytes(Path.Combine(source, file.Replace('/', Path.DirectorySeparatorChar))) : null);

        var temporary = Path.Combine(Path.GetTempPath(), "dbdata-apps-build-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var bundled = new List<string>();
            if (entry is not null)
            {
                var choice = chooseBundler(entry);
                if (choice.Bundler is null) return Fail(choice.Problem ?? "No bundler is available.", load.Manifest, null, entry);
                if (choice.Announcement is not null) Say(choice.Announcement);
                var result = await choice.Bundler.BundleAsync(new AppBundleRequest(source, entry, temporary, Minify: options.Minify, Timeout: options.Timeout, Offline: options.Offline, PackageStore: options.Store, AllowedPackages: options.Allowed, Isolation: options.Isolation, GeneratedFiles: generated), ct);
                if (!result.Succeeded)
                {
                    lines.Add(new AppBuildLine(true, "The build failed:"));
                    foreach (var d in result.Diagnostics) lines.Add(new AppBuildLine(true, "  " + d.Message));
                    return new AppBuildOutcome(false, lines, load.Manifest, 0, 0, [], [.. result.Diagnostics.Select(d => d.Message)], entry);
                }
                bundled.AddRange(result.Files);
                foreach (var note in result.Notes ?? []) Say(note);
            }

            // assemble: the app's own files that are not source, the manifest as app.yaml, and the bundle
            if (Directory.Exists(output)) Directory.Delete(output, true);
            Directory.CreateDirectory(output);
            var entryTop = entry is not null && entry.Contains('/') ? entry[..entry.IndexOf('/')] : null;
            var copied = 0;
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(source, file).Replace(Path.DirectorySeparatorChar, '/');
                if (Path.GetFullPath(file) == manifestPath || IsUnder(output, file) || IsSource(relative, entry, entryTop)) continue;
                var target = Path.Combine(output, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
                copied++;
            }
            foreach (var path in bundled)
            {
                var target = Path.Combine(output, path.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(target)) return Fail($"The bundle writes {path}, and the app already has a file of that name outside its source folder; rename one of them.", load.Manifest, null, entry);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(temporary, path.Replace('/', Path.DirectorySeparatorChar)), target);
            }
            await File.WriteAllTextAsync(Path.Combine(output, "app.yaml"), manifestText, new UTF8Encoding(false), ct);

            // the built-in checks, on the result: what `apps validate` and `apps publish` will see
            var validation = AppValidator.Validate(output, output);
            var errors = validation.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error).ToList();
            foreach (var d in validation.Diagnostics) lines.Add(new AppBuildLine(d.Severity == ConfigSeverity.Error, d.ToString()));
            if (!validation.Ok) return Fail($"{errors.Count} error(s) in the built folder {output}.", load.Manifest, null, entry);
            var total = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Count();
            return new AppBuildOutcome(true, lines, validation.Manifest!, total, copied, bundled, [], entry);
        }
        finally
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
            catch (IOException) { /* a temporary folder left behind is the system's to clean */ }
            catch (UnauthorizedAccessException) { /* the same */ }
        }
    }

    private static bool IsUnder(string folder, string path) => path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>A file of the source tree that is not part of what is served: hidden and tooling files, TypeScript and maps, the entry and the folder it lives in.</summary>
    private static bool IsSource(string relative, string? entry, string? entryTop)
    {
        var parts = relative.Split('/');
        if (parts.Any(p => p.StartsWith('.') || p == "node_modules")) return true;
        if (parts.Length == 1 && ToolingFiles.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) return true;
        if (SourceExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase)) return true;
        if (entry is not null && string.Equals(relative, entry, StringComparison.Ordinal)) return true;
        return entryTop is not null && parts.Length > 1 && parts[0] == entryTop;
    }
}
