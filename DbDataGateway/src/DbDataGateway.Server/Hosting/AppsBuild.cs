using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DbDataKit.Apps;
using DbDataKit.Apps.Bundling;
using DbDataKit.Cli;
using DbDataKit.Config.Yaml;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// <c>apps init</c> and <c>apps build</c> (plan 26, AP23): the two commands that take a person from nothing to a folder <c>apps validate</c> and <c>apps publish</c> accept. <c>init</c> copies one of the sample apps that are inside the
/// program (<c>DbDataGateway/templates/apps</c>, embedded: no repository and no network); <c>build</c> turns an app's source tree into that folder with <see cref="IAppBundler"/>, then runs the built-in checks on the result.
/// Neither opens the Gateway's state, so both run on any machine.
/// </summary>
public static class AppsBuild
{
    /// <summary>The templates, in the order they are listed: what each one is.</summary>
    public static readonly IReadOnlyDictionary<string, string> Templates = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["territory-notes"] = "a code app: index.html, app.js and app.css, no build step",
        ["territory-notes-src"] = "the same code app as a source tree (TypeScript in src/): `apps build` bundles it",
        ["front-portal"] = "a declared portal: views of data the host renders, no script",
        ["territory-views"] = "an app of views only: grid, form, detail and dashboard (view-spec/1) the host draws, no script",
        [NewTemplate] = "the app template: a code app on the default library set (React, the Store controls, the visuals, the typed clients); `apps build` bundles it from the vendored package store",
    };

    /// <summary>The template <c>apps new</c> makes an app from (plan 22a GP32).</summary>
    public const string NewTemplate = "app";

    private const string ResourcePrefix = "apps-templates/";

    /// <summary>Where the entry of a source tree is looked for when <c>--entry</c> is not given.</summary>
    private static readonly string[] EntryConventions = ["src/main.tsx", "src/main.ts", "src/main.jsx", "src/main.js", "src/index.tsx", "src/index.ts", "src/index.jsx", "src/index.js"];

    private static readonly string[] SourceExtensions = [".ts", ".tsx", ".mts", ".cts", ".jsx", ".map"];

    private static readonly string[] ToolingFiles = ["package.json", "tsconfig.json", "bun.lock", "bun.lockb", "package-lock.json", "yarn.lock", "pnpm-lock.yaml"];

    // ---- init ----

    public static async Task<int> InitAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "template", "dir", "name" });
        var usage = $"Usage: apps init --template <{string.Join("|", Templates.Keys)}> --dir <new folder> [--name <app name>]";
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("template") is not { Length: > 0 } template || parsed.Get("dir") is not { Length: > 0 } dir)
        {
            await context.Error.WriteLineAsync(usage);
            foreach (var (name, what) in Templates) await context.Error.WriteLineAsync($"  {name}: {what}");
            return CliExit.Usage;
        }
        return await MakeAsync(template, dir, parsed.Get("name"), context);
    }

    /// <summary><c>apps new</c>: <c>apps init</c> with the app template, which is the one a new app starts from (plan 22a GP32).</summary>
    public static async Task<int> NewAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "dir", "name" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("dir") is not { Length: > 0 } dir)
        {
            await context.Error.WriteLineAsync("Usage: apps new --dir <new folder> [--name <app name>]");
            return CliExit.Usage;
        }
        return await MakeAsync(NewTemplate, dir, parsed.Get("name"), context);
    }

    private static async Task<int> MakeAsync(string template, string dir, string? name2, CliContext context)
    {
        if (!Templates.ContainsKey(template))
        {
            await context.Error.WriteLineAsync($"There is no template '{template}'. The templates are: {string.Join(", ", Templates.Keys)}.");
            return CliExit.Usage;
        }
        if (name2 is { Length: > 0 } && !AppManifestLoader.NameRule.IsMatch(name2))
        {
            await context.Error.WriteLineAsync($"An app's name is lower-case letters, digits, '-' and '_', starting with a letter or digit; '{name2}' is not.");
            return CliExit.Usage;
        }
        var folder = Path.GetFullPath(dir);
        if (File.Exists(folder) || (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()))
        {
            await context.Error.WriteLineAsync($"{folder} is not empty; apps init writes into a new or an empty folder so it never overwrites your files.");
            return CliExit.Failure;
        }

        var files = ReadTemplate(template);
        var written = new List<string>();
        foreach (var (path, bytes) in files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var content = bytes;
            if (path == "app.yaml" && name2 is { Length: > 0 }) content = Encoding.UTF8.GetBytes(Rename(Encoding.UTF8.GetString(bytes), name2));
            var target = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllBytesAsync(target, content, context.Cancellation);
            written.Add(path);
        }
        var built = template.EndsWith("-src", StringComparison.Ordinal) || files.ContainsKey("package.json");   // a source tree (the `-src` sample, the app template) is built; the others are published as they are
        await context.Out.WriteLineAsync($"Made {folder} from the template {template} ({written.Count} files): {string.Join(", ", written)}.");
        await context.Out.WriteLineAsync(built
            ? $"Next: apps build --src {dir} --out <build folder>, then apps validate --dir <build folder> and apps publish --dir <build folder>."
            : $"Next: apps validate --dir {dir}, then apps publish --dir {dir}.");
        context.Data["template"] = template;
        context.Data["dir"] = folder;
        context.Data["files"] = written;
        return CliExit.Ok;
    }

    /// <summary>The template's files by path (relative, with <c>/</c>), read from the program.</summary>
    internal static IReadOnlyDictionary<string, byte[]> ReadTemplate(string template)
    {
        var assembly = typeof(AppsBuild).Assembly;
        var prefix = ResourcePrefix + template + "/";
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var name = resource.Replace('\\', '/');
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            files[name[prefix.Length..]] = memory.ToArray();
        }
        return files.Count > 0 ? files : throw new InvalidOperationException($"The template '{template}' is not in this program.");
    }

    private static string Rename(string manifest, string name)
    {
        var title = char.ToUpperInvariant(name[0]) + name[1..].Replace('-', ' ').Replace('_', ' ');
        manifest = Regex.Replace(manifest, @"^name:.*$", $"name: {name}", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        return Regex.Replace(manifest, @"^title:.*$", $"title: {title}", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    }

    // ---- build ----

    public static Task<int> BuildAsync(string[] args, CliContext context) => BuildAsync(args, context, null, null);

    /// <param name="bundler">The bundler to run; tests pass one so the result does not depend on Bun being installed. When null, Bun is found by <see cref="BunLocator"/> (the <c>--bun</c> option first).</param>
    /// <param name="locate">Finds Bun when <paramref name="bundler"/> is null (tests replace it).</param>
    internal static async Task<int> BuildAsync(string[] args, CliContext context, IAppBundler? bundler, Func<string?, BunLocation>? locate)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { "no-minify", "offline" }, new HashSet<string> { "src", "out", "entry", "manifest", "bun", "timeout" });
        const string usage = "Usage: apps build --src <app source folder> --out <build folder> [--entry <script, default src/main.ts...>] [--manifest <file, default <src>/app.yaml>] [--no-minify] [--offline] [--bun <path>] [--timeout <seconds>]";
        int? seconds = null;
        if (parsed.Get("timeout") is { Length: > 0 } timeoutText)
        {
            if (!int.TryParse(timeoutText, out var n) || n < 1) { await context.Error.WriteLineAsync($"'{timeoutText}' is not a number of seconds."); return CliExit.Usage; }
            seconds = n;
        }
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("src") is not { Length: > 0 } src || parsed.Get("out") is not { Length: > 0 } outArg)
        {
            await context.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }
        var source = Path.GetFullPath(src);
        var output = Path.GetFullPath(outArg);
        var manifestPath = parsed.Get("manifest") is { Length: > 0 } m ? Path.GetFullPath(m) : Path.Combine(source, "app.yaml");
        if (!Directory.Exists(source)) { await context.Error.WriteLineAsync($"{source} is not a folder."); return CliExit.Failure; }
        if (!File.Exists(manifestPath)) { await context.Error.WriteLineAsync($"{manifestPath} does not exist: an app's manifest is app.yaml in its source folder, or name it with --manifest."); return CliExit.Failure; }
        if (output == source || IsUnder(source, output))
        {
            await context.Error.WriteLineAsync($"The build folder {output} is the source folder or holds it; a build replaces its folder, so name another one.");
            return CliExit.Failure;
        }
        if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any() && !File.Exists(Path.Combine(output, "app.yaml")))
        {
            await context.Error.WriteLineAsync($"{output} is not empty and holds no app.yaml, so it does not look like an earlier build. A build replaces its folder; name a new or an empty one.");
            return CliExit.Failure;
        }

        // the manifest first: it is cheap, and its diagnostics name the line
        var manifestText = await File.ReadAllTextAsync(manifestPath, context.Cancellation);
        var load = AppManifestLoader.Load(manifestText, Path.GetFileName(manifestPath));
        foreach (var d in load.Diagnostics) await (d.Severity == ConfigSeverity.Error ? context.Error : context.Out).WriteLineAsync(d.ToString());
        if (!load.Ok) return CliExit.Failure;

        var entry = parsed.Get("entry") is { Length: > 0 } named ? named.Replace('\\', '/') : EntryConventions.FirstOrDefault(c => File.Exists(Path.Combine(source, c.Replace('/', Path.DirectorySeparatorChar))));
        if (entry is null) await context.Out.WriteLineAsync("No script entry (src/main.ts, src/main.js...) and no --entry: the files are copied as they are, nothing is bundled.");

        var temporary = Path.Combine(Path.GetTempPath(), "dbdata-apps-build-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            var bundled = new List<string>();
            if (entry is not null)
            {
                if (bundler is null)
                {
                    var found = (locate ?? (explicitPath => BunLocator.Locate(explicitPath)))(parsed.Get("bun"));
                    if (!found.Found) { await context.Error.WriteLineAsync(found.Problem!); return CliExit.Failure; }
                    await context.Out.WriteLineAsync($"Bundling {entry} with Bun ({found.Source}); package install scripts are not run.");
                    bundler = new BunAppBundler(found.Path!);
                }
                var result = await bundler.BundleAsync(new AppBundleRequest(source, entry, temporary, Minify: !parsed.Has("no-minify"), Timeout: seconds is { } s ? TimeSpan.FromSeconds(s) : null, Offline: parsed.Has("offline")), context.Cancellation);
                if (!result.Succeeded)
                {
                    await context.Error.WriteLineAsync("The build failed:");
                    foreach (var d in result.Diagnostics) await context.Error.WriteLineAsync("  " + d.Message);
                    return CliExit.Failure;
                }
                bundled.AddRange(result.Files);
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
                if (File.Exists(target))
                {
                    await context.Error.WriteLineAsync($"The bundle writes {path}, and the app already has a file of that name outside its source folder; rename one of them.");
                    return CliExit.Failure;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(Path.Combine(temporary, path.Replace('/', Path.DirectorySeparatorChar)), target);
            }
            await File.WriteAllTextAsync(Path.Combine(output, "app.yaml"), manifestText, new UTF8Encoding(false), context.Cancellation);

            // the built-in checks, on the result: what `apps validate` and `apps publish` will see
            var validation = AppValidator.Validate(output, output);
            var errors = validation.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error).ToList();
            foreach (var d in validation.Diagnostics) await (d.Severity == ConfigSeverity.Error ? context.Error : context.Out).WriteLineAsync(d.ToString());
            if (!validation.Ok)
            {
                await context.Error.WriteLineAsync($"{errors.Count} error(s) in the built folder {output}.");
                return CliExit.Failure;
            }
            var total = Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Count();
            var manifest = validation.Manifest!;
            await context.Out.WriteLineAsync($"Built {manifest.Name} {manifest.Version} into {output}: {total} files ({copied} copied, {bundled.Count} bundled).");
            await context.Out.WriteLineAsync($"Next: apps validate --dir {outArg}, then apps publish --dir {outArg}.");
            context.Data["app"] = manifest.Name;
            context.Data["out"] = output;
            context.Data["files"] = total;
            context.Data["bundled"] = bundled;
            return CliExit.Ok;
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
