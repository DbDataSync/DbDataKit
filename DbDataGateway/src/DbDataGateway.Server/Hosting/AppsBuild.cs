using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using DbDataKit.Apps;
using DbDataKit.Apps.Bundling;
using DbDataKit.Cli;
using DbDataKit.Config.Yaml;
using DbDataGateway.Server.Configuration;

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
            if (path == "app.yaml" && name2 is { Length: > 0 }) content = Encoding.UTF8.GetBytes(AppTemplate.Rename(Encoding.UTF8.GetString(bytes), name2));
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
        if (template == NewTemplate) return AppTemplate.Files();   // the app template is in the shared library (the Workspace server makes apps from it too)
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

    // ---- build ----

    public static Task<int> BuildAsync(string[] args, CliContext context) => BuildAsync(args, context, null, null);

    /// <param name="bundler">The bundler to run; tests pass one so the result does not depend on Bun being installed. When null, Bun is found by <see cref="BunLocator"/> (the <c>--bun</c> option first).</param>
    /// <param name="locate">Finds Bun when <paramref name="bundler"/> is null (tests replace it).</param>
    internal static async Task<int> BuildAsync(string[] args, CliContext context, IAppBundler? bundler, Func<string?, BunLocation>? locate)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { "no-minify", "offline" }, new HashSet<string> { "src", "out", "entry", "manifest", "bun", "timeout", "store", "allow", "isolation", "repo" });
        const string usage = "Usage: apps build --src <app source folder> --out <build folder> [--entry <script, default src/main.ts...>] [--manifest <file, default <src>/app.yaml>] [--no-minify] [--offline] [--store <package store folder>] [--allow <package>[,<package>...]] [--isolation <auto|required|none>] [--repo <data directory>] [--bun <path>] [--timeout <seconds>]";
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
        // the package store (plan 22a GP33): --store, or apps.packageStore of the Gateway's server.yaml; with one the build installs nothing and reaches no network
        VendoredPackageStore? store = null;
        IReadOnlyList<string>? allowed = null;
        var isolation = NetworkIsolation.IfAvailable;
        switch (parsed.Get("isolation"))
        {
            case null or "auto": break;
            case "required": isolation = NetworkIsolation.Required; break;
            case "none": isolation = NetworkIsolation.None; break;
            case var other: await context.Error.WriteLineAsync($"--isolation is auto, required or none, not '{other}'."); return CliExit.Usage;
        }
        try
        {
            var explicitStore = parsed.Get("store") is { Length: > 0 } given ? Path.GetFullPath(given) : null;
            // the Gateway's settings are read only when something needs them: the store's folder, or the allow-list
            var settings = explicitStore is null || parsed.Get("allow") is null
                ? GatewayConfiguration.LoadOptions(parsed.Get("repo") is { Length: > 0 } repo ? Path.GetFullPath(repo) : null).Apps
                : null;
            var folder = explicitStore ?? (settings?.PackageStore is { Length: > 0 } fromSettings ? Path.GetFullPath(fromSettings) : null);
            if (folder is not null)
            {
                store = VendoredPackageStore.Load(folder, explicitStore is null ? settings?.PackageStoreSha256 : null);
                allowed = parsed.Get("allow") is { Length: > 0 } list ? list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : settings?.AllowedPackages ?? [];
            }
        }
        catch (VendoredStoreException e) { await context.Error.WriteLineAsync(e.Message); return CliExit.Failure; }
        catch (GatewayConfigException e) { await context.Error.WriteLineAsync(e.Message); return CliExit.Failure; }

        var outcome = await AppSourceBuilder.BuildAsync(
            new AppBuildOptions(src, outArg, parsed.Get("entry"), parsed.Get("manifest"), Minify: !parsed.Has("no-minify"), Timeout: seconds is { } s ? TimeSpan.FromSeconds(s) : null, Offline: parsed.Has("offline"), Store: store, Allowed: allowed, Isolation: isolation),
            entry =>
            {
                if (bundler is not null) return new AppBundlerChoice(bundler);
                var found = (locate ?? (explicitPath => BunLocator.Locate(explicitPath)))(parsed.Get("bun"));
                return found.Found ? new AppBundlerChoice(new BunAppBundler(found.Path!), null, $"Bundling {entry} with Bun ({found.Source}); package install scripts are not run.") : new AppBundlerChoice(null, found.Problem);
            }, context.Cancellation);
        foreach (var line in outcome.Lines) await (line.Error ? context.Error : context.Out).WriteLineAsync(line.Text);
        if (!outcome.Succeeded) return CliExit.Failure;

        var output = Path.GetFullPath(outArg);
        var manifest = outcome.Manifest!;
        await context.Out.WriteLineAsync($"Built {manifest.Name} {manifest.Version} into {output}: {outcome.Files} files ({outcome.Copied} copied, {outcome.Bundled.Count} bundled).");
        await context.Out.WriteLineAsync($"Next: apps validate --dir {outArg}, then apps publish --dir {outArg}.");
        context.Data["app"] = manifest.Name;
        context.Data["out"] = output;
        context.Data["files"] = outcome.Files;
        context.Data["bundled"] = outcome.Bundled;
        return CliExit.Ok;
    }

    // ---- store-verify ----

    /// <summary><c>apps store-verify</c>: checks a package store's files against its <c>store.json</c> and prints what an app may import from it (the library and the allow-list's packages the store holds) and the manifest's digest, which <c>apps.packageStoreSha256</c> can pin.</summary>
    public static async Task<int> StoreVerifyAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "store", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync("Usage: apps store-verify [--store <package store folder>] [--repo <data directory>]");
            return CliExit.Usage;
        }
        try
        {
            var settings = GatewayConfiguration.LoadOptions(parsed.Get("repo") is { Length: > 0 } repo ? Path.GetFullPath(repo) : null).Apps;
            var folder = parsed.Get("store") is { Length: > 0 } given ? Path.GetFullPath(given) : settings.PackageStore is { Length: > 0 } configured ? Path.GetFullPath(configured) : null;
            if (folder is null)
            {
                await context.Error.WriteLineAsync("There is no package store: name one with --store, or set apps.packageStore in server.yaml.");
                return CliExit.Usage;
            }
            var store = VendoredPackageStore.Load(folder);
            var problems = store.Verify();
            foreach (var problem in problems) await context.Error.WriteLineAsync(problem);
            var importable = AppPackagePolicy.Importable(store, settings.AllowedPackages).Order(StringComparer.Ordinal).ToList();
            var missing = settings.AllowedPackages.Where(n => !store.Packages.ContainsKey(n)).ToList();
            foreach (var name in missing) await context.Error.WriteLineAsync($"apps.allowedPackages lists {name}, which is not in the store.");
            if (problems.Count > 0 || missing.Count > 0)
            {
                await context.Error.WriteLineAsync($"The package store {folder} is not as {VendoredPackageStore.ManifestName} says: {problems.Count} file problem(s), {missing.Count} allowed package(s) missing.");
                return CliExit.Failure;
            }
            await context.Out.WriteLineAsync($"The package store {folder} is as {VendoredPackageStore.ManifestName} says: {store.Packages.Count} packages, {store.FileCount} files.");
            await context.Out.WriteLineAsync($"An app may import: {string.Join(", ", importable)}.");
            await context.Out.WriteLineAsync($"Digest of {VendoredPackageStore.ManifestName} (apps.packageStoreSha256): {store.Digest}");
            context.Data["store"] = folder;
            context.Data["packages"] = store.Packages.Count;
            context.Data["files"] = store.FileCount;
            context.Data["digest"] = store.Digest;
            return CliExit.Ok;
        }
        catch (VendoredStoreException e) { await context.Error.WriteLineAsync(e.Message); return CliExit.Failure; }
        catch (GatewayConfigException e) { await context.Error.WriteLineAsync(e.Message); return CliExit.Failure; }
    }
}
